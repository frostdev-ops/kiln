// Settings' map and search (pages/settings/model.ts, searchIndex.ts): every deep link in use resolves to its section and
// group, /settings picks the right section, search matches names, help, More text and keywords (accents folded), and
// every row a section draws has its words in the index (a row without them would not show).
import { expect, test } from "bun:test";
import { readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";
import type { AppInfo, SchemaSnapshot } from "../src/api/commands";
import snapshot from "../src/data/kiln-json.snapshot.json";
import { GROUPS, SECTIONS, bestHit, countBySection, fold, keyWords, markParts, matchItem, queryWords, resolveAnchor, search, sectionFor, snippet, type SettingsCtx } from "../src/pages/settings/model";
import { KILN_GAME_PATHS, buildIndex, forceHint, kilnGroupOf, kilnSchema } from "../src/pages/settings/searchIndex";

const SRC = join(import.meta.dirname, "..", "src");
const schema = snapshot as unknown as SchemaSnapshot;

/** Every source file under src/. */
function files(dir: string): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap((d) => (d.isDirectory() ? files(join(dir, d.name)) : /\.tsx?$/.test(d.name) ? [join(dir, d.name)] : []));
}

test("every /settings#<anchor> the launcher links to opens its section at its group", () => {
  const anchors = new Set<string>();
  for (const f of files(SRC)) for (const m of readFileSync(f, "utf8").matchAll(/["'`]\/settings#([a-z-]+)/g)) anchors.add(m[1]);
  expect([...anchors].sort()).toEqual(["nexus", "profiles"]);
  // and the ones the old page had (Card ids) and the README's map
  for (const [a, section] of [["profiles", "mods"], ["crucible", "mods"], ["library", "mods"], ["nexus", "online"], ["graphics", "graphics"],
    ["upscalers", "graphics"], ["shroudforge", "game"], ["data-folder", "mods"], ["kiln-json", "advanced"]] as const) {
    expect(resolveAnchor(a)?.section, a).toBe(section);
    expect(resolveAnchor(a)?.group, a).not.toBeNull();
  }
  expect(resolveAnchor("advanced")).toEqual({ section: "advanced", group: null });
  expect(resolveAnchor("nothing-here")).toBeNull();
});

test("/settings opens a route's section, a group's anchor's, else the last one shown, else General", () => {
  expect(sectionFor(undefined, "", null)).toBe("general");
  expect(sectionFor(undefined, "", "graphics")).toBe("graphics");
  expect(sectionFor(undefined, "", "bogus")).toBe("general");
  expect(sectionFor("online", "", "graphics")).toBe("online");
  expect(sectionFor(undefined, "#profiles", "graphics")).toBe("mods");
  expect(sectionFor("general", "#profiles", null)).toBe("mods");
  expect(sectionFor("advanced", "#unknown", null)).toBe("advanced");
  expect(SECTIONS).toEqual(["general", "mods", "game", "graphics", "online", "advanced"]);
  // a group's id is its anchor: one of each
  expect(new Set(GROUPS.map((g) => g.id)).size).toBe(GROUPS.length);
});

const info = {
  version: "0.2.0-dev", kiln: "", platform: "linux", arch: "x64", dataDir: "/d", commit: null, mock: true,
  render: { mode: "gpu", why: "default", marker: false, x11: { auto: true, on: false, why: "wayland" }, force: { on: false, step: "off", why: "off", blocked: true } },
  sandbox: { marker: false, webkit: "unused", why: "default" }, desktop: { on: true, installed: true, file: "/a/kiln.desktop", foreign: false },
  deck: { detected: false, signals: [] },
} as unknown as AppInfo;
// (festive: the festive season's switch offered, as in the season)
const full: SettingsCtx = { info, dist: "direct", nexus: true, crucible: true, tauri: true, client: true, schema, values: { graphics: { upscaler: "amd" } }, festive: true };

test("search: every word in the name, the help, the More text or a keyword; accents folded; in the page's order", () => {
  const items = buildIndex(full);
  const ids = (q: string) => search(items, q).map((i) => i.id);
  expect(ids("upscaler")).toEqual(expect.arrayContaining(["upscaler.choice", "upscalers"]));
  expect(search(items, "upscaler").every((i) => i.section === "graphics")).toBe(true);
  expect(ids("FSR")).toContain("upscaler.amd");
  expect(ids("animated fire")).toEqual(["fire"]);
  // only in the More text
  const weak = search(items, "weak GPUs");
  expect(weak.map((i) => i.id)).toEqual(["fire"]);
  expect(matchItem(weak[0], queryWords("weak GPUs"))).toEqual({ in: "more" });
  // a kiln.json key, the rows' own words
  expect(ids("graphics.fpsCap")).toEqual(["kj:graphics.fpsCap"]);
  expect(ids("ui.chatHistory")).toEqual(["kj:ui.chatHistory"]);
  // Enter opens a hit by its name before one by its help or More text
  expect(bestHit(search(items, "upscaler"), "upscaler")?.id).toBe("upscaler.choice");
  expect(bestHit([], "x")).toBeNull();
  expect(ids("")).toEqual([]);
  expect(ids("zzzz")).toEqual([]);
  expect(countBySection(search(items, "sandbox"))).toEqual({ advanced: 1 });
  expect(fold("Ünïcödé ÉTÉ")).toBe("unicode ete");
  expect(queryWords("  Frame   CAP ")).toEqual(["frame", "cap"]);
  // the order: sections, then groups, then rows
  const order = search(items, "the").map((i) => SECTIONS.indexOf(i.section));
  expect(order).toEqual([...order].sort((a, b) => a - b));
});

test("the marks and the quote of a More text", () => {
  expect(markParts("Upscaler files", ["upscal"])).toEqual([{ text: "Upscal", hit: true }, { text: "er files", hit: false }]);
  expect(markParts("Frame cap", [])).toEqual([{ text: "Frame cap", hit: false }]);
  expect(snippet("The fire on Play (WebGL). Off: a plain Play button, easier on weak GPUs.", ["weak"])).toContain("easier on weak GPUs.");
  expect(snippet("short", ["nothing"])).toBeNull();
  expect(keyWords("ui.chatHistory")).toBe("Chat History");
});

test("kiln.json: every entry has one place (or a control of its own), and Game & Kiln shows its own in order", () => {
  const places = schema.entries.map((e) => [e.path, kilnGroupOf(e.path)] as const);
  expect(places.filter(([, g]) => g === null).map(([p]) => p).sort()).toEqual(["graphics.amdUpscaler", "graphics.upscaler", "shroudforge.useInstalled", "ui.language"]);
  expect(kilnSchema(schema, "kiln").entries.map((e) => e.path)).toEqual(KILN_GAME_PATHS);
  expect(kilnSchema(schema, "kilnjson").entries.map((e) => e.path)).toEqual(expect.arrayContaining(["policy", "log_level", "ui.opacity", "ui.overlay", "mods.crash_reports"]));
  expect(kilnSchema(schema, "graphics", new Set(["kj:graphics.readout"])).entries.map((e) => e.path)).toEqual(["graphics.readout"]);
});

test("every row a section draws has its words in the index", () => {
  const sections = join(SRC, "pages", "settings", "sections");
  const rows = new Set<string>();
  for (const f of [...readdirSync(sections).map((n) => join(sections, n)), join(SRC, "components", "NexusSettings.tsx"), join(SRC, "pages", "settings", "sections", "Mods.tsx")])
    for (const m of readFileSync(f, "utf8").matchAll(/<SettingRow id="([^"]+)"/g)) rows.add(m[1]);
  expect(rows.size).toBeGreaterThan(25);
  const indexed = new Set(buildIndex(full).map((i) => i.id));
  expect([...rows].filter((r) => !indexed.has(r))).toEqual([]);
  // and every item points at a group of its own section
  for (const i of buildIndex(full)) expect(GROUPS.find((g) => g.id === i.group)?.section, i.id).toBe(i.section);
  // the Nexus build's update line
  expect(buildIndex({ ...full, dist: "nexus" }).some((i) => i.id === "updates.nexus")).toBe(true);
});

test("Force GPU drawing on NVIDIA: found by NVIDIA, off by default, and it says what this start does", () => {
  expect(search(buildIndex(full), "NVIDIA").map((i) => i.id)).toContain("forceDmabuf");
  expect(search(buildIndex(full), "WEBKIT_FORCE_DMABUF_RENDERER").map((i) => i.id)).toEqual(["forceDmabuf"]);
  const turnedOff = "The launcher turned this off by itself: its start at 2026-11-22T21:04:10Z ended before its page had started";
  const hints = {
    blocked: forceHint({ on: false, step: "off", why: "off", blocked: true }),
    off: forceHint({ on: false, step: "off", why: "off", blocked: false }),
    on: forceHint({ on: true, step: "shm", why: "on", blocked: false }),
    failed: forceHint({ on: true, step: "off", why: "failed", blocked: true }),
    turnedOff: forceHint({ on: false, step: "off", why: "off", blocked: true, failedWhy: turnedOff }),
  };
  for (const h of Object.values(hints)) {
    expect(h).toContain("It is off on clean starts");
    expect(h).toContain("WEBKIT_DMABUF_RENDERER_FORCE_SHM=1");
    // never the other way round, never root
    expect(h).not.toMatch(/WEBKIT_DISABLE_DMABUF_RENDERER|chmod|sudo|\broot\b/);
  }
  expect(hints.blocked).toContain("because of that block: turn this on to try the GPU");
  expect(hints.off).toContain("leaves the block as it is");
  expect(hints.on).toContain("draws past the block");
  expect(hints.failed).toContain("The start before with it failed, so this start forces nothing and the switch goes off.");
  // the launcher's note says why it turned the switch off, while the switch is off
  expect(hints.turnedOff).toContain(`${turnedOff}.`);
  expect(forceHint({ on: true, step: "shm", why: "on", blocked: false, failedWhy: turnedOff })).not.toContain(turnedOff);
});
