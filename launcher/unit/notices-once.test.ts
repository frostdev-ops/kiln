// An activation's notices about mod folders come with every activation (a mod switched, Play, a profile loaded): each
// sentence is raised once per game (shell/notices.ts, the backend's notices_unseen; here the browser mock's), a new or
// changed one still shows, and with "Show notes about mod folders" off only a folder that may still run a mod that is
// off is raised. What the last activation said stays readable (useFolderNotes).
import { beforeEach, expect, test } from "bun:test";
import * as api from "../src/api/commands";
import type { Activation } from "../src/api/commands";
import { notifyActivation, useFolderNotes } from "../src/shell/notices";
import { reportShown, useToasts } from "../src/ui/Toast";

beforeEach(() => useToasts.setState({ items: [] }));

let n = 0;
/** A fresh game id per test: the mock's memory lives as long as the module. */
const act = (r: Partial<Activation>, gameId = `g-${++n}`): Activation => ({
  gameId, profileId: "ember-run", generation: 9, pathMode: "z", profileJson: "/x", copied: 0, fingerprint: "kms1:00", activated: "2026-11-12T10:00:00Z", ...r,
});
/** What the toasts say; they are on screen now, as the Toaster would report them. */
const shown = () => {
  const items = useToasts.getState().items;
  reportShown(items);
  return items.map((i) => `${i.title}|${i.detail}`);
};
/** Lets the reports of what was on screen reach the (mock) backend. */
const settle = () => new Promise((ok) => setTimeout(ok, 0));
const OWN = "shroudforge:hoardershelper: the game uses your own folder /game/mods/HoardersHelper for this mod; Kiln leaves it as it is and places no copy of its own.";
const KEPT = { path: "/game/mods/edited", reason: "files Kiln placed were changed. It is off in the profile, but ShroudForge may still load it.", mayRun: true, off: true };

test("the same notes are raised once per game, a new or changed sentence still shows", async () => {
  const a = act({ warnings: [OWN], keptFiles: ["eml:a: kept 1 file"], retained: [KEPT] });
  await notifyActivation(a);
  expect(shown().map((s) => s.split("|")[0]).sort()).toEqual(["Mod settings kept", "Notes on your mods", "Some mod folders were kept"]);
  await settle();
  useToasts.setState({ items: [] });
  // the next mod switch writes the profile again and says the same
  await notifyActivation({ ...a, generation: 10 });
  expect(shown()).toEqual([]);
  // one new sentence: only it
  await notifyActivation({ ...a, warnings: [OWN, "shroudforge:other: a new note"] });
  expect(shown()).toEqual(["Notes on your mods|shroudforge:other: a new note"]);
  await settle();
  // another game has its own memory
  await notifyActivation({ ...a, gameId: "another-game" });
  expect(shown().length).toBe(4);
  // what the last activation said stays readable
  expect(useFolderNotes.getState().byGame[a.gameId].warnings).toEqual([OWN, "shroudforge:other: a new note"]);
});

test("with the notes switched off only a folder that may still run a mod that is off comes, once", async () => {
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
    reportShown(items);
    await settle();
    useToasts.setState({ items: [] });
    await notifyActivation(act({ warnings: [OWN], retained: [KEPT, on] }, "g-off"));
    expect(shown()).toEqual([]);
  } finally {
    await api.appSettingsSet(s);
  }
});

test("a notice that never was on screen comes again at the next activation", async () => {
  const a = act({ warnings: [OWN], retained: [KEPT] });
  await notifyActivation(a);
  expect(useToasts.getState().items.length).toBe(2);
  // only the first was shown (the Deck shows one dialog at a time), then the launcher closed
  reportShown([useToasts.getState().items[0]]);
  await settle();
  useToasts.setState({ items: [] });
  await notifyActivation(a);
  expect(useToasts.getState().items.map((i) => i.title)).toEqual(["Notes on your mods"]);
});

test("with the notes switched off, the player's own folder of a mod that is off still comes once", async () => {
  const s = await api.appSettingsGet();
  await api.appSettingsSet({ ...s, modFolderNotes: false });
  try {
    const off = "shroudforge:hoardershelper: your own folder /game/mods/HoardersHelper stays as it is; Kiln never switches a folder it did not place.";
    const a = act({ warnings: [OWN, off], safetyNotes: [off] });
    await notifyActivation(a);
    expect(shown()).toEqual([`Notes on your mods|${off}`]);
    await settle();
    useToasts.setState({ items: [] });
    await notifyActivation(a);
    expect(shown()).toEqual([]);
  } finally {
    await api.appSettingsSet(s);
  }
});
