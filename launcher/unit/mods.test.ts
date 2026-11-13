// The main list's rows and count (src/shell/mods.ts): every kind counts, the profile decides on and off, a game-folder
// mod is a row without a switch, and the sentence names only the exceptions a player must know.
import { expect, test } from "bun:test";
import type { BootState, FoundMod, LibraryEntry, ModKind, Profile, ProfileMod, ResolvePreview } from "../src/api/commands";
import { emptyMods } from "../src/api/kinds";
import { buildRows, countLine, filterRows, kindsOf, type ModInputs } from "../src/shell/mods";
import { bootLine } from "../src/deck/status";

const hash = (s: string) => `kdh1:${s.padEnd(64, "0").slice(0, 64)}`;
const lib = (kind: ModKind, id: string, name = id, versions = ["1.0.0"]): LibraryEntry => ({
  kind, id, name, versions: versions.map((v) => ({ version: v, hash: hash(id + v), folder: `library/${kind}/${id}/${v}`, imported: "2026-11-01T00:00:00Z", side: "both", clients: "mod",
    size: 1000, source: null, manifestId: null, manifest: null })),
});
const pm = (id: string, enabled = true, version = "1.0.0"): ProfileMod => ({ id, version, hash: hash(id + version), enabled });
const profile = (mods: Partial<Record<ModKind, ProfileMod[]>>): Profile => ({
  format: 1, id: "p", name: "P", created: "", updated: "", generation: 1, notes: "", mods: { ...emptyMods(), ...mods }, dropins: [], dataDir: "shared", server: null,
});
const found = (name: string, state: FoundMod["state"], kind: ModKind | null = "eml"): FoundMod => ({ kind, name, id: name, version: null, path: `mods/${name}`, state, reason: `${name} is ${state}` });
const inputs = (x: Partial<ModInputs>): ModInputs => ({ library: [], profile: null, preview: null, found: [], launch: null, retained: [], xhlHost: null, ...x });

test("every kind counts: a player with eight mods of other loaders and one Kiln mod reads nine", () => {
  const kinds: ModKind[] = ["eml", "shroudforge", "set", "flame", "greeneye", "enshwick", "shroudlink", "emf"];
  const library = [lib("kiln", "one-kiln-mod"), ...kinds.map((k) => lib(k, `${k}-mod`))];
  const p = profile(Object.fromEntries([["kiln", [pm("one-kiln-mod")]], ...kinds.map((k) => [k, [pm(`${k}-mod`)]])]));
  const rows = buildRows(inputs({ library, profile: p }));
  expect(rows).toHaveLength(9);
  expect(countLine(rows).text).toBe("9 mods on.");
  expect(kindsOf(rows)).toEqual(["kiln", "emf", "eml", "shroudforge", "set", "greeneye", "flame", "enshwick", "shroudlink"]);
});

test("in the Library but not in the profile is off; in the profile, disabled, is off and keeps its entry", () => {
  const rows = buildRows(inputs({ library: [lib("eml", "a"), lib("eml", "b"), lib("kiln", "c")], profile: profile({ eml: [pm("a", false)] }) }));
  expect(rows.map((r) => [r.id, r.on, !!r.entry])).toEqual([["a", false, true], ["b", false, false], ["c", false, false]]);
  expect(countLine(rows).text).toBe("0 mods on, 3 off.");
});

test("a profile entry the Library lacks is a row that says so, and will not run", () => {
  const rows = buildRows(inputs({ library: [], profile: profile({ shroudforge: [pm("gone")] }) }));
  expect(rows[0]).toMatchObject({ id: "gone", on: true, lib: null, note: { text: "Not in the library" } });
  expect(countLine(rows).text).toBe("1 mod on. gone will not run: it is not in the library any more.");
});

test("game-folder mods: rows without a switch; loading ones count on, not-loading ones off, copies and non-mods not at all", () => {
  const rows = buildRows(inputs({ library: [lib("kiln", "k")], profile: profile({ kiln: [pm("k")] }),
    found: [found("Loads", "loads"), found("Off", "off"), found("Copy", "inProfile"), found("Screens", "cannotRun", null)] }));
  expect(rows.map((r) => r.name)).toEqual(["k", "Copy", "Loads", "Off", "Screens"]); // the Library's first, then the folder's
  expect(rows.filter((r) => r.found).every((r) => !r.on && !r.entry)).toBe(true);
  expect(countLine(rows)).toMatchObject({ on: 2, off: 1, text: "2 mods on, 1 off." });
});

test("the sentence names the world mods, the ones off in this game and the ones that will not run, with the reason", () => {
  const preview: ResolvePreview = {
    side: "client", native: [], warnings: [], withWorld: ["stacks"],
    kiln: { order: ["items"], rejected: [
      { id: "stacks", dir: "stacks", state: "skipped", reason: "Starts with your own world" },
      { id: "hotbars", dir: "hotbars", state: "failed", reason: "missing dependency ui-lib ^1.0" },
      { id: "fog", dir: "fog", state: "skipped", reason: "Off in this game: its kiln.json turns off mods that start with your world" },
    ] },
  };
  const library = ["items", "stacks", "hotbars", "fog"].map((id) => lib("kiln", id, id[0].toUpperCase() + id.slice(1)));
  const rows = buildRows(inputs({ library, preview, profile: profile({ kiln: library.map((l) => pm(l.id)) }) }));
  expect(countLine(rows).text).toBe("4 mods on. Stacks starts with your world. Fog stays off in this game. Hotbars will not run: it needs ui-lib ^1.0.");
  expect(rows.find((r) => r.id === "hotbars")!.note).toMatchObject({ text: "Needs ui-lib ^1.0", tone: "warn" });
  expect(rows.find((r) => r.id === "stacks")!.note).toMatchObject({ text: "Starts with your world" });
});

test("Ember mods do not run yet; an XHL companion needs XHL-Core only when the game has none", () => {
  const library = [lib("ember", "chests", "Private Chests"), lib("xhl", "vein", "Vein Mining")];
  const p = profile({ ember: [pm("chests")], xhl: [pm("vein")] });
  expect(countLine(buildRows(inputs({ library, profile: p, xhlHost: false }))).text)
    .toBe("2 mods on. Private Chests does not run yet: Ember has published no mod ABI yet. Vein Mining will not run: it runs only with XHL-Core, installed separately.");
  expect(countLine(buildRows(inputs({ library, profile: p, xhlHost: true }))).text).toBe("2 mods on. Private Chests does not run yet: Ember has published no mod ABI yet.");
});

test("a failure of the last start is the row's note, when the same run wrote it", () => {
  const boot = { pid: 7, written: "2026-11-11T10:00:00Z" } as BootState["boot"];
  const launch = { boot, stale: false, native: null, mods: { format: 1, pid: 7, written: "2026-11-11T10:00:01Z", generation: 1, mods: [{ id: "k", state: "failed", reason: "threw in OnLoad" }] } } as BootState;
  const rows = buildRows(inputs({ library: [lib("kiln", "k")], profile: profile({ kiln: [pm("k")] }), launch }));
  expect(rows[0].note).toMatchObject({ text: "Failed at the last start", tone: "err", title: "threw in OnLoad" });
  const other = { ...launch, mods: { ...launch.mods!, pid: 8 } };
  expect(buildRows(inputs({ library: [lib("kiln", "k")], profile: profile({ kiln: [pm("k")] }), launch: other }))[0].note).toBeNull();
});

test("an off mod whose placed copy the last activation left in the game folder says so", () => {
  const rows = buildRows(inputs({ library: [lib("eml", "loot-x")], profile: profile({ eml: [pm("loot-x", false)] }),
    retained: [{ path: "/games/Enshrouded/mods/loot-x", reason: "its files changed since Kiln placed them", mayRun: true }] }));
  expect(rows[0].note).toMatchObject({ text: "Left in the game folder", title: "its files changed since Kiln placed them" });
});

test("the filter matches words in the name or id, and one kind", () => {
  const rows = buildRows(inputs({ library: [lib("kiln", "big-stacks", "Big Stacks"), lib("shroudforge", "better-chests", "Better Chests"), lib("shroudlink", "chestsort", "chestsort.dll")] }));
  expect(filterRows(rows, "chest", "").map((r) => r.id)).toEqual(["better-chests", "chestsort"]);
  expect(filterRows(rows, "", "kiln").map((r) => r.id)).toEqual(["big-stacks"]);
  expect(filterRows(rows, "STACK", "shroudforge")).toEqual([]);
});

test("the Deck's last boot line counts no partial kind of mods", () => {
  const boot = { pid: 1, written: "2026-11-11T10:00:00Z", chain: [], plugins: [], profile: { mode: "active" } } as unknown as BootState["boot"];
  expect(bootLine({ boot, mods: null, native: null, stale: false }).text).toBe("Booted, no errors");
  expect(bootLine({ boot, mods: null, native: null, stale: false, log: { errors: 2, first: [] } }).text).toBe("Booted, 2 errors");
});

test("a mod changed in its Library folder says so, on or off, with the version the row uses to take over", () => {
  const changed = lib("eml", "hook", "钩爪拉力2倍", ["2.0.0", "1.0.0"]);
  changed.versions[1] = { ...changed.versions[1], changed: true };
  // the profile uses 1.0.0, the changed one
  const rows = buildRows(inputs({ library: [changed, lib("eml", "other")], profile: profile({ eml: [pm("hook", true, "1.0.0")] }) }));
  expect(rows.find((r) => r.id === "hook")).toMatchObject({ on: true, note: { text: "Changed on disk", tone: "warn" },
    changed: { kind: "eml", id: "hook", version: "1.0.0", hash: hash("hook1.0.0") } });
  expect(rows.find((r) => r.id === "other")).toMatchObject({ changed: null });
  // not in the profile: the newest version is the one the row shows, unchanged here
  expect(buildRows(inputs({ library: [changed] }))[0]).toMatchObject({ changed: null, note: null });
});
