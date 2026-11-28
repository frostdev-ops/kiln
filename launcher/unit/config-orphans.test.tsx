// The Configs window's files of mods that are not installed (0.2.0-dev.31.31; HTR, #general: Mods, More, Configs listed the
// configs of mods he had removed, and their switches looked live). The backend marks them (`orphaned`); the window groups
// them under "Not installed: kept for when you add it back", greys their controls (the form and the raw file), and offers
// Clear on each and Clear all on the group. Installed entries are as they were.
import { expect, test } from "bun:test";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { renderToStaticMarkup } from "react-dom/server";
import type { ConfigDocument, ConfigFileInfo, ConfigTarget, SchemaSnapshot } from "../src/api/commands";
import { qk } from "../src/api/hooks";
import { ConfigEditor } from "../src/components/ConfigEditor";
import { clearedWords, configName, pickConfig, splitConfigs } from "../src/shell/configOrphans";
import { t } from "../src/i18n";

const info = (file: string, mod: string | null, orphaned: boolean, more: Partial<ConfigFileInfo> = {}): ConfigFileInfo =>
  ({ file, mod, kind: file.startsWith("emf/") ? "emf" : "kiln", hasSchema: mod !== null, modVersion: null, modified: null, orphaned, ...more });

const LIST = [
  info("big-stacks.json", "big-stacks", false),
  info("emf/beastmaster.ini", "beastmaster", true),
  info("native.json", null, false, { kind: "native" }),
  info("expeditions.json", "expeditions", true),
  info("mystery.json", null, false, { kind: "kiln" }),
];

const schema = (file: string, mod: string): SchemaSnapshot => ({
  format: 1, source: "kiln", file, id: mod, mod, sections: [],
  entries: [
    { path: "enabled", type: "bool", comment: "Switch it on", default: true, requiresRestart: false, synced: false },
    { path: "limit", type: "int", comment: "How many", default: 3, requiresRestart: false, synced: false, min: 1, max: 9 },
  ],
}) as SchemaSnapshot;
const docOf = (file: string, mod: string): ConfigDocument => ({ file, text: '{ "enabled": true, "limit": 4 }', values: { enabled: true, limit: 4 }, schema: schema(file, mod), errors: [] });

const target: ConfigTarget = { profile: "work" };
function render(list: ConfigFileInfo[], opts: { initial?: string; target?: ConfigTarget; readOnly?: boolean } = {}) {
  const tg = opts.target ?? target;
  const qc = new QueryClient();
  qc.setQueryData(qk.configs(tg), list);
  for (const f of list) if (f.mod) qc.setQueryData(qk.config(tg, f.file), docOf(f.file, f.mod));
  return renderToStaticMarkup(<QueryClientProvider client={qc}><ConfigEditor target={tg} initial={opts.initial} readOnly={opts.readOnly} /></QueryClientProvider>);
}
const disabledControls = (html: string) => (html.match(/<input[^>]*disabled/g) ?? []).length;

test("the two groups, in the list's order", () => {
  const { installed, orphaned } = splitConfigs(LIST);
  expect(installed.map((f) => f.file)).toEqual(["big-stacks.json", "native.json", "mystery.json"]);
  expect(orphaned.map((f) => f.file)).toEqual(["emf/beastmaster.ini", "expeditions.json"]);
  expect(splitConfigs([])).toEqual({ installed: [], orphaned: [] });
});

test("an orphan never opens first, but one asked for, or picked, does", () => {
  const onlyOrphans = [info("a.json", "a", true), info("b.json", "b", true)];
  expect(pickConfig(LIST, null)).toBe("big-stacks.json");
  expect(pickConfig([info("x.json", "x", true), info("y.json", "y", false)], null)).toBe("y.json");
  expect(pickConfig(onlyOrphans, null)).toBe("a.json");
  expect(pickConfig(LIST, null, "expeditions")).toBe("expeditions.json");
  expect(pickConfig(LIST, null, "emf/beastmaster.ini")).toBe("emf/beastmaster.ini");
  expect(pickConfig(LIST, "expeditions.json", "big-stacks")).toBe("expeditions.json");
  // a picked file that is gone (just cleared) falls back to what opens first
  expect(pickConfig(LIST.filter((f) => f.file !== "expeditions.json"), "expeditions.json")).toBe("big-stacks.json");
  expect(pickConfig([], null)).toBeNull();
  expect(configName(LIST[1])).toBe("beastmaster");
  expect(configName(LIST[2])).toBe("native.json");
});

test("Clear says where the copy went, or that nothing was there", () => {
  expect(clearedWords({ removed: ["a"], backup: "/data/backups/mod-settings/config-work-20261128T120000Z", notes: [] }, t))
    .toEqual({ title: "Settings cleared", detail: "A copy is in /data/backups/mod-settings/config-work-20261128T120000Z." });
  expect(clearedWords({ removed: [], backup: null, notes: [] }, t)).toEqual({ title: "Nothing to clear", detail: "Those files were already gone." });
});

test("a profile's list groups the files of mods that are not installed, with Clear on each and Clear all", () => {
  const html = render(LIST, { initial: "big-stacks" });
  expect(html).toContain("Not installed: kept for when you add it back");
  expect(html).toContain("Clear all");
  expect(html).toContain('aria-label="Clear the settings of beastmaster"');
  expect(html).toContain('aria-label="Clear the settings of expeditions"');
  // the group comes after the installed files and holds only the orphans
  const head = html.indexOf("Not installed: kept for when you add it back");
  for (const file of ["big-stacks.json", "native.json", "mystery.json"]) expect(html.indexOf(file)).toBeLessThan(head);
  for (const file of ["emf/beastmaster.ini", "expeditions.json"]) expect(html.indexOf(file)).toBeGreaterThan(head);
  // no Clear on an installed file's row
  expect(html).not.toContain('aria-label="Clear the settings of big-stacks"');
  expect((html.match(/k-config-orphan"/g) ?? []).length).toBe(2);
});

test("an orphan's switches and fields are read-only, with a hint, and Clear in its card", () => {
  const html = render(LIST, { initial: "expeditions" });
  expect(html).toContain("expeditions is not installed. Its settings are kept for when you add it back.");
  expect(html).toContain("changes do nothing until the mod is back");
  expect(html).toContain(">Clear settings<");
  // the bool switch and the number box are disabled
  expect(disabledControls(html)).toBeGreaterThanOrEqual(2);
});

test("an installed mod's file is as it was: no hint, live controls", () => {
  const html = render(LIST, { initial: "big-stacks" });
  expect(html).not.toContain("is not installed");
  expect(html).not.toContain(">Clear settings<");
  expect(disabledControls(html)).toBe(0);
});

test("with nothing orphaned the window is unchanged: no group, no Clear", () => {
  const html = render(LIST.map((f) => ({ ...f, orphaned: false })), { initial: "big-stacks" });
  expect(html).not.toContain("Not installed");
  expect(html).not.toContain("Clear all");
  expect(html).not.toContain("k-config-orphan");
});

test("a read-only window (and a server's, which is never marked) offers no Clear", () => {
  const ro = render(LIST, { readOnly: true, initial: "expeditions" });
  expect(ro).toContain("Not installed: kept for when you add it back");
  expect(ro).not.toContain("Clear all");
  expect(ro).not.toContain(">Clear settings<");
  const server = render(LIST, { target: { server: "lab" }, initial: "expeditions" });
  expect(server).not.toContain("Clear all");
  expect(server).not.toContain("Clear the settings of");
});
