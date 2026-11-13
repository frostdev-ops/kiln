// "All on" and "All off" above the main list (src/shell/mods.ts, player report of 13 Nov 2026): they act on the rows
// the list shows, change only what a single switch would change, say what they left out and why, edit the profile as
// one change, and an Undo puts back exactly what they changed (not what changed since).
import { expect, test } from "bun:test";
import type { LibraryEntry, ModKind, Profile, ProfileMod } from "../src/api/commands";
import { emptyMods } from "../src/api/kinds";
import { applyBulk, buildRows, bulkPlan, filterRows, revertBulk, type ModInputs } from "../src/shell/mods";

const hash = (s: string) => `kdh1:${s.padEnd(64, "0").slice(0, 64)}`;
const lib = (kind: ModKind, id: string, versions = ["1.0.0"]): LibraryEntry => ({
  kind, id, name: id, versions: versions.map((v) => ({ version: v, hash: hash(id + v), folder: `library/${kind}/${id}/${v}`, imported: "2026-11-01T00:00:00Z", side: "both", clients: "mod",
    size: 1000, source: null, manifestId: null, manifest: null })),
});
const pm = (id: string, enabled = true, version = "1.0.0"): ProfileMod => ({ id, version, hash: hash(id + version), enabled });
const profile = (mods: Partial<Record<ModKind, ProfileMod[]>>): Profile => ({
  format: 1, id: "p", name: "P", created: "", updated: "", generation: 1, notes: "", mods: { ...emptyMods(), ...mods }, dropins: [], dataDir: "shared", server: null,
});
const inputs = (x: Partial<ModInputs>): ModInputs => ({ library: [], profile: null, preview: null, found: [], launch: null, retained: [], xhlHost: null, ...x });
const clone = (p: Profile): Profile => JSON.parse(JSON.stringify(p));

const fixture = () => {
  const p = profile({ kiln: [pm("a", true), pm("b", false)], eml: [pm("gone", false)] });
  const rows = buildRows(inputs({
    library: [lib("kiln", "a"), lib("kiln", "b"), lib("kiln", "c"), lib("eml", "d")],
    profile: p,
    found: [{ kind: "eml", name: "Folder Mod", id: "folder-mod", version: null, path: "mods/folder-mod", state: "loads", reason: "" }],
  }));
  return { p, rows };
};

test("All on: the rows that are off, a mod the profile lacks goes in with the Library's newest; a game-folder row is left out with why", () => {
  const { p, rows } = fixture();
  const plan = bulkPlan(rows, true);
  expect(plan.change.map((c) => `${c.kind}/${c.after.id}`).sort()).toEqual(["eml/d", "eml/gone", "kiln/b", "kiln/c"]);
  expect(plan.left).toEqual([{ name: "Folder Mod", why: "in the game folder, it has no switch" }]);
  const next = clone(p);
  applyBulk(next, plan.change);
  expect(next.mods.kiln).toEqual([pm("a"), pm("b"), pm("c")]);
  expect(next.mods.eml).toEqual([pm("gone"), pm("d")]);
});

test("All off: only the rows that are on; nothing to do when every shown row is off already", () => {
  const { p, rows } = fixture();
  const plan = bulkPlan(rows, false);
  expect(plan.change.map((c) => c.after)).toEqual([{ ...pm("a"), enabled: false }]);
  const next = clone(p);
  applyBulk(next, plan.change);
  expect(next.mods.kiln.map((m) => m.enabled)).toEqual([false, false]);
  expect(bulkPlan(filterRows(rows, "", "eml").filter((r) => !r.found), false).change).toEqual([]);
});

test("a filter narrows what they act on", () => {
  const { rows } = fixture();
  expect(bulkPlan(filterRows(rows, "", "kiln"), true).change.map((c) => c.after.id)).toEqual(["b", "c"]);
});

test("a row the profile lacks with no Library version cannot be switched on, and says so", () => {
  const empty: LibraryEntry = { ...lib("kiln", "hollow"), versions: [] };
  const rows = buildRows(inputs({ library: [empty], profile: profile({}) }));
  expect(bulkPlan(rows, true)).toEqual({ change: [], left: [{ name: "hollow", why: "no version of it is in the Library" }] });
});

test("Undo puts back what the action changed, and keeps a mod changed since", () => {
  const { p, rows } = fixture();
  const plan = bulkPlan(rows, true);
  const after = clone(p);
  applyBulk(after, plan.change);
  // the player switches c off again by hand before Undo
  after.mods.kiln = after.mods.kiln.map((m) => (m.id === "c" ? { ...m, enabled: false } : m));
  const back = clone(after);
  expect(revertBulk(back, plan.change)).toBe(1);
  expect(back.mods.kiln).toEqual([pm("a"), pm("b", false), { ...pm("c"), enabled: false }]);
  expect(back.mods.eml).toEqual([pm("gone", false)]);
});
