// The one list of mods the main screen and the Deck's Mods screen show (docs/kiln/launcher/app.md, "Play"): every
// Library entry of every kind, the loaded profile's entries the Library lacks, and the mods a player put in the game
// folder that the launcher did not place (kiln_manage::found). A row is on when its mod is in the loaded profile and
// enabled; a game-folder row has no switch (only its state and fix). One short note per row, only when there is
// something to say. Pure: shell/useMods.ts reads the queries and the edits.
import type { BootState, FoundMod, LibraryEntry, LibraryRef, ModKind, Profile, ProfileMod, ResolvePreview, RetainedPlacement } from "../api/commands";
import { MOD_KINDS, NOT_RUNNING_KINDS } from "../api/kinds";

export type NoteTone = "info" | "warn" | "err" | "idle";
export interface ModNote { text: string; tone: NoteTone; title?: string }

export interface ModRow {
  /** "<kind>/<id>" for a Library or profile entry, "found:<path>" for a mod in the game folder. */
  key: string;
  kind: ModKind | null;
  id: string;
  name: string;
  /** The version in the profile, else the Library's newest; a game-folder mod's own (null when it gives none). */
  version: string | null;
  /** In the loaded profile and enabled. */
  on: boolean;
  /** The loaded profile's entry (on or off), null when the profile does not list it. */
  entry: ProfileMod | null;
  /** The Library's entry, null when the Library lacks it (a profile entry whose files are gone, a game-folder mod). */
  lib: LibraryEntry | null;
  /** A mod in the game folder that the launcher did not place. */
  found: FoundMod | null;
  note: ModNote | null;
  /** On, and it will not run in this game: why, in a few words (the count's exceptions). */
  wontRun: string | null;
  /** The Library version this row uses (the profile's, else the newest) was changed in its folder since its import:
   *  the entry to give library_take_over ("Use the changed files"). */
  changed: LibraryRef | null;
  /** On, a Kiln mod for the server side: it starts with the player's own world. */
  withWorld: boolean;
  /** On, and it stays off in this game (the game's kiln.json turns off mods that start with your world). */
  staysOff: boolean;
}

export interface ModInputs {
  library: LibraryEntry[];
  /** The loaded profile; null: none is loaded (or not read yet). */
  profile: Profile | null;
  /** The client preview of the loaded profile for this game (profile_resolve). */
  preview: ResolvePreview | null;
  /** found_scan of the loaded profile. */
  found: FoundMod[];
  /** launch_state: the last start's mods.json and native.json. */
  launch: BootState | null;
  /** Folders the last activation left in the game folder (Activation.retained). */
  retained: RetainedPlacement[];
  /** XHL-Core recognized in this game (engine_status); null: not known. */
  xhlHost: boolean | null;
}

const lower = (s: string) => s.toLowerCase();
const byName = (a: ModRow, b: ModRow) => a.name.localeCompare(b.name, undefined, { sensitivity: "base" }) || a.key.localeCompare(b.key);

/** "missing dependency ui-lib ^1.0" → "Needs ui-lib ^1.0". */
function needs(reason: string): string | null {
  const m = /missing (?:dependency|requirement) ([^\s,;]+)(?: ([^\s,;]+))?/i.exec(reason);
  return m ? `Needs ${m[1]}${m[2] ? ` ${m[2]}` : ""}` : null;
}

/** The start before this one wrote mods.json and native.json: their failures, by id, when they belong to boot.json's run. */
function lastStartFailures(st: BootState | null): Map<string, string> {
  const out = new Map<string, string>();
  const boot = st?.boot;
  if (!boot) return out;
  const since = Date.parse(boot.written) - 2000;
  const same = (f: { pid: number; written: string } | null | undefined) => !!f && f.pid === boot.pid && Date.parse(f.written) >= since;
  if (same(st?.mods)) for (const m of st!.mods!.mods) if (m.state === "failed") out.set(`kiln/${m.id}`, m.reason ?? "failed");
  if (same(st?.native)) for (const m of st!.native!.mods) if (m.state === "failed") out.set(`native/${m.id}`, m.reason ?? "failed");
  return out;
}

const NOT_YET: Partial<Record<ModKind, string>> = { ember: "Ember has published no mod ABI yet" };

/** The rows, Library and profile entries first (by name), then the game folder's (by name). */
export function buildRows(x: ModInputs): ModRow[] {
  const p = x.profile;
  const rejected = new Map((x.preview?.kiln.rejected ?? []).map((r) => [r.id, r]));
  const world = new Set(x.preview?.withWorld ?? []);
  const native = new Map((x.preview?.native ?? []).map((n) => [n.id, n]));
  const failed = lastStartFailures(x.launch);
  const retained = x.retained.map((r) => ({ base: lower(r.path.split(/[\\/]/).filter(Boolean).pop() ?? ""), r }));
  const rows: ModRow[] = [];
  const seen = new Set<string>();

  const add = (kind: ModKind, id: string, lib: LibraryEntry | null, entry: ProfileMod | null) => {
    const key = `${kind}/${id}`;
    if (seen.has(key)) return;
    seen.add(key);
    const on = !!entry?.enabled;
    const row: ModRow = {
      key, kind, id, name: lib?.name ?? id, version: entry?.version ?? lib?.versions[0]?.version ?? null, on, entry, lib, found: null,
      note: null, wontRun: null, withWorld: false, staysOff: false, changed: null,
    };
    if (on) {
      const fail = failed.get(key);
      if (!lib) {
        row.wontRun = "it is not in the library any more";
        row.note = { text: "Not in the library", tone: "err", title: "Its files are gone from the Library. Add it again, or remove it." };
      } else if (NOT_RUNNING_KINDS.includes(kind)) {
        row.wontRun = NOT_YET[kind] ?? "its loader cannot start it yet";
        row.note = { text: "Does not run yet", tone: "idle", title: `${row.wontRun}. It stays in your list.` };
      } else if (kind === "xhl" && x.xhlHost === false) {
        row.wontRun = "it runs only with XHL-Core, installed separately";
        row.note = { text: "Needs XHL-Core", tone: "warn", title: "XHL companions run only through XHL-Core. Kiln never loads their DLLs." };
      } else if (kind === "kiln" && x.preview) {
        const r = rejected.get(id);
        if (world.has(id)) {
          row.withWorld = true;
          row.note = { text: "Starts with your world", tone: "info", title: r?.reason };
        } else if (r && r.state === "skipped") {
          row.staysOff = true;
          row.note = { text: "Off in this game", tone: "warn", title: r.reason };
        } else if (r && r.state !== "disabled") {
          const n = needs(r.reason);
          row.wontRun = n ? n.replace(/^Needs/, "it needs") : r.reason;
          row.note = { text: n ?? "Will not run", tone: "warn", title: r.reason };
        }
      } else if (kind === "native") {
        const why = native.get(id)?.reason;
        if (why) { row.wontRun = why; row.note = { text: "Will not run", tone: "warn", title: why }; }
      }
      if (fail) row.note = { text: "Failed at the last start", tone: "err", title: fail };
    } else {
      const names = [lower(id), ...(lib?.versions.map((v) => lower(v.manifestId ?? "")) ?? [])].filter(Boolean);
      const kept = retained.find((k) => names.includes(k.base));
      if (kept) row.note = { text: "Left in the game folder", tone: "warn", title: kept.r.reason };
    }
    // its files were changed in its Library folder (a mod.json edited by hand): what Play would refuse, so it comes first
    const used = entry ? lib?.versions.find((v) => v.hash === entry.hash) : lib?.versions[0];
    if (used?.changed) {
      row.changed = { kind, id, version: used.version, hash: used.hash };
      row.note = { text: "Changed on disk", tone: "warn",
        title: "Its files were changed in its folder since Kiln imported them. Use the changed files to load them, with the name and info they give now." };
    }
    rows.push(row);
  };

  for (const kind of MOD_KINDS) {
    const entries = p?.mods[kind] ?? [];
    for (const l of x.library.filter((e) => e.kind === kind)) add(kind, l.id, l, entries.find((m) => m.id === l.id) ?? null);
    for (const m of entries) add(kind, m.id, null, m);
  }
  rows.sort(byName);

  const folder: ModRow[] = x.found.map((f) => ({
    key: `found:${f.path}`, kind: f.kind, id: f.id ?? f.name, name: f.name, version: f.version, on: false, entry: null, lib: null, found: f,
    note: foundNote(f), wontRun: null, withWorld: false, staysOff: false, changed: null,
  }));
  folder.sort(byName);
  return [...rows, ...folder];
}

const FOUND_WORD: Record<FoundMod["state"], { text: string; tone: NoteTone }> = {
  loads: { text: "Loads from the game folder", tone: "idle" },
  off: { text: "Does not load", tone: "warn" },
  inProfile: { text: "A copy is in your list", tone: "idle" },
  cannotRun: { text: "Can't run", tone: "idle" },
};
function foundNote(f: FoundMod): ModNote {
  return { ...FOUND_WORD[f.state], title: f.reason };
}

/** What the count counts: a game-folder mod counts on when it loads, off when it does not; a copy of a listed mod and
 *  what cannot run (not a mod) do not count. */
export function isOn(r: ModRow): boolean {
  return r.found ? r.found.state === "loads" : r.on;
}
export function isOff(r: ModRow): boolean {
  return r.found ? r.found.state === "off" : !r.on;
}

/** "Big Stacks", "Big Stacks and Night Sky", "Big Stacks, Night Sky and 2 more". */
export function names(list: string[]): string {
  return list.length <= 2 ? list.join(" and ") : `${list.slice(0, 2).join(", ")} and ${list.length - 2} more`;
}

/** The count over every kind, then only the exceptions a player must know: "12 mods on, 3 off. Big Stacks starts with
 *  your world. More Hotbars will not run: it needs ui-lib ^1.0." */
export function countLine(rows: ModRow[]): { on: number; off: number; text: string } {
  const on = rows.filter(isOn).length;
  const off = rows.filter(isOff).length;
  const head = `${on} mod${on === 1 ? "" : "s"} on${off ? `, ${off} off` : ""}.`;
  const parts = [head];
  const world = rows.filter((r) => r.withWorld).map((r) => r.name);
  if (world.length) parts.push(`${names(world)} ${world.length === 1 ? "starts" : "start"} with your world.`);
  const stays = rows.filter((r) => r.staysOff).map((r) => r.name);
  if (stays.length) parts.push(`${names(stays)} ${stays.length === 1 ? "stays" : "stay"} off in this game.`);
  const yet = rows.filter((r) => r.on && r.kind && NOT_RUNNING_KINDS.includes(r.kind));
  if (yet.length) parts.push(`${names(yet.map((r) => r.name))} ${yet.length === 1 ? "does" : "do"} not run yet: ${yet[0].wontRun}.`);
  const wont = rows.filter((r) => r.wontRun && !(r.kind && NOT_RUNNING_KINDS.includes(r.kind)));
  if (wont.length === 1) parts.push(`${wont[0].name} will not run: ${wont[0].wontRun}.`);
  else if (wont.length > 1) parts.push(`${names(wont.map((r) => r.name))} will not run in this game.`);
  return { on, off, text: parts.join(" ") };
}

/** The kinds present, in ModKind::ALL order (the kind filter is offered when there is more than one). */
export function kindsOf(rows: ModRow[]): ModKind[] {
  return MOD_KINDS.filter((k) => rows.some((r) => r.kind === k));
}

/** One entry "All on" or "All off" changes: as it was (null: the profile lacked it) and as it becomes. */
export interface BulkChange { kind: ModKind; before: ProfileMod | null; after: ProfileMod }
export interface BulkPlan {
  /** The rows whose switch changes, as profile changes. */
  change: BulkChange[];
  /** Rows a switch could not change either, by name, with why. */
  left: { name: string; why: string }[];
}

/** "All on" / "All off" over the rows the list shows: exactly what each row's own switch would do (useMods.toggle),
 *  for the rows not in that state already. A game-folder mod has no switch; a mod the profile lacks needs a Library
 *  version to go in. */
export function bulkPlan(rows: ModRow[], on: boolean): BulkPlan {
  const plan: BulkPlan = { change: [], left: [] };
  for (const r of rows) {
    if (r.found || !r.kind) { plan.left.push({ name: r.name, why: "in the game folder, it has no switch" }); continue; }
    if (r.on === on) continue;
    if (r.entry) { plan.change.push({ kind: r.kind, before: r.entry, after: { ...r.entry, enabled: on } }); continue; }
    const v = r.lib?.versions[0];
    if (!v) { plan.left.push({ name: r.name, why: "no version of it is in the Library" }); continue; }
    plan.change.push({ kind: r.kind, before: null, after: { id: r.id, version: v.version, hash: v.hash, enabled: true } });
  }
  return plan;
}

/** Applies the plan to the profile (the queue's newest copy): an entry keeps its version and settings and only
 *  switches; a new one goes in. */
export function applyBulk(p: Profile, changes: BulkChange[]): void {
  for (const c of changes) {
    const list = p.mods[c.kind] ?? [];
    const i = list.findIndex((m) => m.id === c.after.id);
    if (i >= 0) list[i] = { ...list[i], enabled: c.after.enabled };
    else list.push(c.after);
    p.mods[c.kind] = list;
  }
}

/** Undo: every entry the plan changed and nobody changed since goes back (out again when the profile lacked it).
 *  Returns how many were changed since and kept. */
export function revertBulk(p: Profile, changes: BulkChange[]): number {
  let kept = 0;
  for (const c of changes) {
    const list = p.mods[c.kind] ?? [];
    const i = list.findIndex((m) => m.id === c.after.id);
    const m = list[i];
    if (!m || m.enabled !== c.after.enabled || m.version !== c.after.version || m.hash !== c.after.hash) { kept++; continue; }
    if (c.before) list[i] = { ...m, enabled: c.before.enabled };
    else list.splice(i, 1);
    p.mods[c.kind] = list;
  }
  return kept;
}

/** The filter: words in the name or id, and one kind ("" every kind). */
export function filterRows(rows: ModRow[], text: string, kind: ModKind | ""): ModRow[] {
  const q = lower(text.trim());
  return rows.filter((r) => (!kind || r.kind === kind) && (!q || lower(`${r.name} ${r.id}`).includes(q)));
}
