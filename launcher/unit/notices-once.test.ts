// An activation's notices about mod folders come with every activation (a mod switched, Play, a profile loaded): each
// sentence is raised until the player closes its notice, then not again for that game (shell/notices.ts, the backend's
// notices_unseen / notices_shown; here the browser mock's). A new or changed one still shows; with "Show notes about
// mod folders" off only a folder that may still run a mod that is off is raised. What the last activation said stays
// readable (useFolderNotes).
import { beforeEach, expect, test } from "bun:test";
import * as api from "../src/api/commands";
import type { Activation } from "../src/api/commands";
import { notifyActivation, useFolderNotes } from "../src/shell/notices";
import { toast, useToasts } from "../src/ui/Toast";

beforeEach(() => useToasts.setState({ items: [] }));

let n = 0;
/** A fresh game id per test: the mock's memory lives as long as the module. */
const act = (r: Partial<Activation>, gameId = `g-${++n}`): Activation => ({
  gameId, profileId: "ember-run", generation: 9, pathMode: "z", profileJson: "/x", copied: 0, fingerprint: "kms1:00", activated: "2026-11-12T10:00:00Z", ...r,
});
const said = () => useToasts.getState().items.map((i) => `${i.title}|${i.detail}`);
/** Lets the reports of what the player closed reach the (mock) backend. */
const settle = () => new Promise((ok) => setTimeout(ok, 0));
/** The player closes every notice on screen (their close buttons). */
const closeAll = async () => {
  for (const i of useToasts.getState().items) useToasts.getState().close(i.id);
  await settle();
};
/** The launcher closes: what is on screen goes, nobody closed it. */
const quit = () => useToasts.setState({ items: [] });
const OWN = "shroudforge:hoardershelper: the game uses your own folder /game/mods/HoardersHelper for this mod; Kiln leaves it as it is and places no copy of its own.";
const KEPT = { path: "/game/mods/edited", reason: "files Kiln placed were changed. It is off in the profile, but ShroudForge may still load it.", mayRun: true, off: true };

test("the same notes are raised once per game once closed, a new or changed sentence still shows", async () => {
  const a = act({ warnings: [OWN], keptFiles: ["eml:a: kept 1 file"], retained: [KEPT] });
  await notifyActivation(a);
  expect(said().map((s) => s.split("|")[0]).sort()).toEqual(["Mod settings kept", "Notes on your mods", "Some mod folders were kept"]);
  await closeAll();
  // the next mod switch writes the profile again and says the same
  await notifyActivation({ ...a, generation: 10 });
  expect(said()).toEqual([]);
  // one new sentence: only it
  await notifyActivation({ ...a, warnings: [OWN, "shroudforge:other: a new note"] });
  expect(said()).toEqual(["Notes on your mods|shroudforge:other: a new note"]);
  await closeAll();
  // another game has its own memory
  await notifyActivation({ ...a, gameId: "another-game" });
  expect(said().length).toBe(3);
  // what the last activation said stays readable
  expect(useFolderNotes.getState().byGame[a.gameId].warnings).toEqual([OWN, "shroudforge:other: a new note"]);
});

test("a notice on screen but not closed comes again after the launcher closed; still open, it is not added twice", async () => {
  const a = act({ warnings: [OWN], retained: [KEPT] });
  await notifyActivation(a);
  expect(said().length).toBe(2);
  // the same activation again while both are open: no second copy, no "(2 times)"
  await notifyActivation(a);
  expect(useToasts.getState().items.map((i) => [i.title, i.count ?? 1])).toEqual([["Some mod folders were kept", 1], ["Notes on your mods", 1]]);
  quit();
  await settle();
  await notifyActivation(a);
  expect(said().length).toBe(2);
  // the player closes the folder notice only
  useToasts.getState().close(useToasts.getState().items[0].id);
  await settle();
  quit();
  await notifyActivation(a);
  expect(useToasts.getState().items.map((i) => i.title)).toEqual(["Notes on your mods"]);
});

test("closing the entry the older notices folded into marks every one of them", async () => {
  const a = act({ warnings: [OWN], keptFiles: ["eml:a: kept 1 file"], retained: [KEPT] });
  await notifyActivation(a);
  for (let i = 0; i < 9; i++) toast.info(`Other ${i}`, `other notice ${i}`, { ms: 0 });
  const fold = useToasts.getState().items.find((i) => i.folded);
  expect(fold?.folded?.map((f) => f.title)).toContain("Notes on your mods");
  expect(fold?.folded?.map((f) => f.title)).toContain("Some mod folders were kept");
  useToasts.getState().close(fold!.id);
  await settle();
  quit();
  await notifyActivation(a);
  expect(said()).toEqual([]);
});

test("the same sentence of two games is one notice, and closing it marks it for both", async () => {
  const a = act({ warnings: [OWN] });
  const b = act({ warnings: [OWN] });
  await notifyActivation(a);
  await notifyActivation(b);
  expect(useToasts.getState().items.length).toBe(1);
  await closeAll();
  quit();
  await notifyActivation(a);
  await notifyActivation(b);
  expect(said()).toEqual([]);
});

test("with the notes switched off only a folder that may still run a mod that is off comes, until closed", async () => {
  const s = await api.appSettingsGet();
  await api.appSettingsSet({ ...s, modFolderNotes: false });
  try {
    const on = { ...KEPT, path: "/game/mods/on-but-changed", off: false };
    await notifyActivation(act({ warnings: [OWN], retained: [KEPT, on] }, "g-off"));
    const items = useToasts.getState().items;
    expect(items.map((i) => i.title)).toEqual(["Some mod folders were kept"]);
    expect(String(items[0].detail)).toContain("/game/mods/edited");
    expect(String(items[0].detail)).not.toContain("on-but-changed");
    expect(items[0].persistent).toBe(true);
    await closeAll();
    await notifyActivation(act({ warnings: [OWN], retained: [KEPT, on] }, "g-off"));
    expect(said()).toEqual([]);
  } finally {
    await api.appSettingsSet(s);
  }
});

test("with the notes switched off, the player's own folder of a mod that is off still comes once", async () => {
  const s = await api.appSettingsGet();
  await api.appSettingsSet({ ...s, modFolderNotes: false });
  try {
    const off = "shroudforge:hoardershelper: your own folder /game/mods/HoardersHelper stays as it is; Kiln never switches a folder it did not place.";
    const a = act({ warnings: [OWN, off], safetyNotes: [off] });
    await notifyActivation(a);
    expect(said()).toEqual([`Notes on your mods|${off}`]);
    await closeAll();
    await notifyActivation(a);
    expect(said()).toEqual([]);
  } finally {
    await api.appSettingsSet(s);
  }
});
