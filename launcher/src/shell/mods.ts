import { stopIds } from "./stoppedStart";
// The one list of mods the main screen and the Deck's Mods screen show (docs/kiln/launcher/app.md, "Play"): every
// Library entry of every kind, the loaded profile's entries the Library lacks, and the mods a player put in the game
// folder that the launcher did not place (kiln_manage::found). A row is on when its mod is in the loaded profile and
// enabled; a game-folder row has no switch (only its state and fix). One short note per row, only when there is
// something to say. Pure: shell/useMods.ts reads the queries and the edits.
import type { BootState, FoundMod, LibraryEntry, LibraryRef, LibraryVersion, ModKind, NativePlanEntry, Profile, ProfileMod, ResolvePreview, RetainedPlacement } from "../api/commands";
import { MOD_KINDS, NOT_RUNNING_KINDS } from "../api/kinds";
import { t } from "../i18n";

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

/** The version the launcher picks by itself for a mod (the row's version when the profile lacks it, a switch, All on,
 *  the server's sync dialog): the Library's newest that is not kept for a roll back. A kept version is only ever
 *  picked by hand (Roll back to, Use). None when only kept versions are left. */
export function defaultVersion(lib: LibraryEntry | null | undefined): LibraryVersion | undefined {
  return lib?.versions.find((v) => !v.kept);
}
const byName = (a: ModRow, b: ModRow) => a.name.localeCompare(b.name, undefined, { sensitivity: "base" }) || a.key.localeCompare(b.key);

/** "missing dependency ui-lib ^1.0" → "ui-lib ^1.0" (the note says "Needs ui-lib ^1.0"). */
function needs(reason: string): string | null {
  const m = /missing (?:dependency|requirement) ([^\s,;]+)(?: ([^\s,;]+))?/i.exec(reason);
  return m ? `${m[1]}${m[2] ? ` ${m[2]}` : ""}` : null;
}

/** A native entry of the preview that runs from the game folder: Kiln places its package there at activation and the
 *  game loads it itself (a powrprof.dll plugin loader, .asi plugins), not Kiln. It is on, and its reason says how
 *  (kiln_manage::profile::NativePlanEntry); an entry that is off has the reason it does not load. */
export function fromGameFolder(n: NativePlanEntry): boolean {
  return n.enabled && !!n.reason;
}

/** "placed in the game folder at activation: …" → "Placed in the game folder at activation: ….". */
function asSentence(s: string): string {
  const x = s.trim();
  return x ? `${x[0].toUpperCase()}${x.slice(1)}${/[.!?]$/.test(x) ? "" : "."}` : x;
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

/** Why a kind's mods do not run yet, when there is a word for it. */
const notYet = (kind: ModKind): string | undefined => (kind === "ember" ? t("shell.mods.emberNoAbi") : undefined);

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
      key, kind, id, name: lib?.name ?? id, version: entry?.version ?? defaultVersion(lib)?.version ?? null, on, entry, lib, found: null,
      note: null, wontRun: null, withWorld: false, staysOff: false, changed: null,
    };
    if (on) {
      const fail = failed.get(key);
      if (!lib) {
        row.wontRun = t("shell.mods.notInLibraryWhy");
        row.note = { text: t("shell.mods.notInLibrary"), tone: "err", title: t("shell.mods.notInLibraryTip") };
      } else if (NOT_RUNNING_KINDS.includes(kind)) {
        row.wontRun = notYet(kind) ?? t("shell.mods.loaderCannot");
        row.note = { text: t("shell.mods.notYet"), tone: "idle", title: t("shell.mods.notYetTip", { why: row.wontRun }) };
      } else if (kind === "xhl" && x.xhlHost === false) {
        row.wontRun = t("shell.mods.xhlWhy");
        row.note = { text: t("shell.mods.needsXhl"), tone: "warn", title: t("shell.mods.xhlTip") };
      } else if (kind === "kiln" && x.preview) {
        const r = rejected.get(id);
        if (world.has(id)) {
          row.withWorld = true;
          row.note = { text: t("shell.mods.withWorld"), tone: "info", title: r?.reason };
        } else if (r && r.state === "skipped") {
          row.staysOff = true;
          row.note = { text: t("shell.mods.offHere"), tone: "warn", title: r.reason };
        } else if (r && r.state !== "disabled") {
          const dep = needs(r.reason);
          row.wontRun = dep ? t("shell.mods.itNeeds", { dep }) : r.reason;
          row.note = { text: dep ? t("shell.mods.needs", { dep }) : t("shell.mods.willNotRun"), tone: "warn", title: r.reason };
        }
      } else if (kind === "native") {
        const n = native.get(id);
        if (n?.reason && fromGameFolder(n)) row.note = { text: t("shell.mods.fromGameFolder"), tone: "info", title: asSentence(n.reason) };
        else if (n?.reason) { row.wontRun = n.reason; row.note = { text: t("shell.mods.willNotRun"), tone: "warn", title: n.reason }; }
      }
      if (fail) row.note = { text: t("shell.mods.failedLast"), tone: "err", title: fail };
    } else {
      const names = [lower(id), ...(lib?.versions.map((v) => lower(v.manifestId ?? "")) ?? [])].filter(Boolean);
      const kept = retained.find((k) => names.includes(k.base));
      if (kept) row.note = { text: t("shell.mods.leftInFolder"), tone: "warn", title: kept.r.reason };
    }
    // its files were changed in its Library folder (a mod.json edited by hand): what Play would refuse, so it comes first
    const used = entry ? lib?.versions.find((v) => v.hash === entry.hash) : defaultVersion(lib);
    // an EML mod whose package carried a modified EML loader (Holster Toggle's dbghelp.dll): only its EML part runs
    if (on && !row.note && used?.loaderPart) {
      row.note = { text: t("shell.mods.partly"), tone: "warn", title: asSentence(used.loaderPart) };
    }
    if (used?.changed) {
      row.changed = { kind, id, version: used.version, hash: used.hash };
      row.note = { text: t("shell.mods.changed"), tone: "warn", title: t("shell.mods.changedTip") };
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

function foundWord(state: FoundMod["state"]): { text: string; tone: NoteTone } {
  switch (state) {
    case "loads": return { text: t("shell.mods.foundLoads"), tone: "idle" };
    case "off": return { text: t("shell.mods.foundOff"), tone: "warn" };
    case "inProfile": return { text: t("shell.mods.foundInProfile"), tone: "idle" };
    case "cannotRun": return { text: t("shell.mods.foundCannotRun"), tone: "idle" };
  }
}
function foundNote(f: FoundMod): ModNote {
  return { ...foundWord(f.state), title: f.reason };
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
  if (list.length <= 1) return list.join("");
  if (list.length === 2) return t("shell.mods.namesTwo", { a: list[0], b: list[1] });
  return t("shell.mods.namesMore", { a: list[0], b: list[1], more: list.length - 2 });
}

/** The count over every kind, then only the exceptions a player must know: "12 mods on, 3 off. Big Stacks starts with
 *  your world. More Hotbars will not run: it needs ui-lib ^1.0." */
export function countLine(rows: ModRow[]): { on: number; off: number; text: string } {
  const on = rows.filter(isOn).length;
  const off = rows.filter(isOff).length;
  const head = off ? t("shell.mods.countOnOff", { count: on, off }) : t("shell.mods.countOn", { count: on });
  const parts = [head];
  const world = rows.filter((r) => r.withWorld).map((r) => r.name);
  if (world.length) parts.push(t("shell.mods.countWorld", { names: names(world), count: world.length }));
  const stays = rows.filter((r) => r.staysOff).map((r) => r.name);
  if (stays.length) parts.push(t("shell.mods.countStays", { names: names(stays), count: stays.length }));
  const yet = rows.filter((r) => r.on && r.kind && NOT_RUNNING_KINDS.includes(r.kind));
  if (yet.length) parts.push(t("shell.mods.countYet", { names: names(yet.map((r) => r.name)), count: yet.length, why: yet[0].wontRun }));
  const wont = rows.filter((r) => r.wontRun && !(r.kind && NOT_RUNNING_KINDS.includes(r.kind)));
  if (wont.length === 1) parts.push(t("shell.mods.countWontOne", { name: wont[0].name, why: wont[0].wontRun }));
  else if (wont.length > 1) parts.push(t("shell.mods.countWontMany", { names: names(wont.map((r) => r.name)) }));
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
    if (r.found || !r.kind) { plan.left.push({ name: r.name, why: t("shell.mods.bulkNoSwitch") }); continue; }
    if (r.on === on) continue;
    if (r.entry) { plan.change.push({ kind: r.kind, before: r.entry, after: { ...r.entry, enabled: on } }); continue; }
    const v = defaultVersion(r.lib);
    if (!v) { plan.left.push({ name: r.name, why: r.lib?.versions.length ? t("shell.mods.bulkOnlyKept") : t("shell.mods.bulkNoVersion") }); continue; }
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

/** "<kind>/<id>": how an Undo names the mods the player touched since the action (ModRow.key's form). */
export const changeKey = (c: { kind: ModKind; after: { id: string } }) => `${c.kind}/${c.after.id}`;

/** Undo: every entry the plan changed goes back (out again when the profile lacked it), except the ones in `touched`
 *  (the player switched, re-versioned or removed them since: their final values cannot tell an edit back and forth)
 *  and any whose entry is not as the plan left it. Returns how many were kept. */
export function revertBulk(p: Profile, changes: BulkChange[], touched: ReadonlySet<string> = new Set()): number {
  let kept = 0;
  for (const c of changes) {
    const list = p.mods[c.kind] ?? [];
    const i = list.findIndex((m) => m.id === c.after.id);
    const m = list[i];
    if (touched.has(changeKey(c)) || !m || m.enabled !== c.after.enabled || m.version !== c.after.version || m.hash !== c.after.hash) { kept++; continue; }
    if (c.before) list[i] = { ...m, enabled: c.before.enabled };
    else list.splice(i, 1);
    p.mods[c.kind] = list;
  }
  return kept;
}

/** The filter: words in the name or id, and one kind ("" every kind). */
export function filterRows(rows: ModRow[], text: string, kind: ModKind | ""): ModRow[] {
  const ids = stopIds(text);
  const q = lower(text.trim());
  return rows.filter((r) => (!kind || r.kind === kind) && (ids ? ids.includes(r.id) : (!q || lower(`${r.name} ${r.id}`).includes(q))));
}
