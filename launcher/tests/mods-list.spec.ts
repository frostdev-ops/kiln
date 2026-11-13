// The main screen's one list of every mod (docs/kiln/launcher/app.md, Play): every Library entry of every kind with a
// switch, the mods found in the game folder, a count over every kind, profiles as a background feature in Settings,
// the old routes sent to the new places. Browser mock only: the seeded launcher (src/api/mock.ts) has "Ember run"
// loaded in the Steam game, a Library of 22 mods over 14 kinds, and 7 entries in the mock game folder.
import { expect, test, type Page } from "@playwright/test";

async function desktop(page: Page, route = "/") {
  await page.goto(`/#${route}`);
  await page.locator(".k-splash").waitFor({ state: "detached" });
}
const api = <T,>(page: Page, fn: string, ...args: unknown[]) =>
  page.evaluate(async ([f, a]) => {
    const m = (await import("/src/api/commands.ts")) as unknown as Record<string, (...x: unknown[]) => Promise<unknown>>;
    return m[f as string](...(a as unknown[]));
  }, [fn, args] as const) as Promise<T>;
type Mods = Record<string, { id: string; enabled: boolean }[]>;
const profileMods = async (page: Page, id = "ember-run") => (await api<{ mods: Mods }>(page, "profileGet", id)).mods;
const list = (page: Page) => page.locator(".k-hero").getByRole("list", { name: "Mods" });
const row = (page: Page, name: string) => list(page).getByRole("listitem").filter({ has: page.getByText(name, { exact: true }) });
const say = (page: Page) => page.locator(".k-hero .k-hero-say");

test("the main screen lists every mod of every kind, and the count covers them all", async ({ page }) => {
  await desktop(page);
  // Ember run: 4 Kiln, 3 native, 1 EMF, 1 ShroudForge, 1 Greeneye, 1 Shroudtopia on (11) and the game folder's SET mod,
  // which loads by itself (12); off: the 11 other Library mods and the 4 game-folder mods that do not load
  await expect(say(page)).toHaveText(/^12 mods on, 15 off\./, { timeout: 10_000 });
  const kinds: [string, string][] = [["New Items", "Kiln"], ["Creative Mode", "Native"], ["Beastmaster", "EMF"], ["my-plugin.dll", "Plugin"], ["Loot Tables X", "EML"],
    ["Better Chests", "ShroudForge"], ["FOV Tweak", "SET"], ["Zoom Out", "Greeneye"], ["Creative Build", "Flame"], ["Lanterns", "Enshwick"], ["chestsort.dll", "shroudlink"],
    ["private-chests", "Ember"], ["XHL-Vein-Mining", "XHL"]];
  for (const [name, kind] of kinds) await expect(row(page, name).first(), name).toContainText(new RegExp(kind, "i"));
  await expect(row(page, "Better Chests").getByRole("switch", { name: "Better Chests" })).toHaveAttribute("aria-checked", "true");
  await expect(row(page, "Loot Tables X").getByRole("switch", { name: "Loot Tables X" })).toHaveAttribute("aria-checked", "false");
  // a mod found in the game folder is a row too, marked so, with no switch the backend could not honour
  const hoarder = row(page, "Hoarder's Helper");
  await expect(hoarder).toContainText("Game folder");
  await expect(hoarder.getByRole("switch")).toHaveCount(0);
  // the old sentence and Kiln-only counts are gone
  await expect(page.locator(".k-hero")).not.toContainText("mods load on this client");
  await expect(page.locator(".k-hero")).not.toContainText(/Kiln mods in this profile/);
  // the exceptions a player must know follow the count: two Kiln mods Kiln will not load; Big Stacks, which would start
  // with the player's world, is off in Ember run, so it is not named
  await expect(say(page)).toHaveText("12 mods on, 15 off. More Hotbars and Night Sky will not run in this game.");
  await expect(row(page, "More Hotbars")).toContainText("Needs ui-lib ^1.0");
});

test("switching a mod off and on changes the loaded profile, and survives a reload", async ({ page }) => {
  await desktop(page);
  await page.evaluate(() => sessionStorage.setItem("kiln.mock.persist", "1"));
  await page.reload();
  await page.locator(".k-splash").waitFor({ state: "detached" });
  const sw = () => row(page, "Better Chests").getByRole("switch", { name: "Better Chests" });
  await expect(sw()).toHaveAttribute("aria-checked", "true", { timeout: 10_000 });
  await sw().click();
  await expect(sw()).toHaveAttribute("aria-checked", "false");
  await expect.poll(async () => (await profileMods(page)).shroudforge).toEqual([expect.objectContaining({ id: "better-chests", enabled: false })]);
  await expect(say(page)).toHaveText(/^11 mods on, 16 off\./);
  await page.reload();
  await page.locator(".k-splash").waitFor({ state: "detached" });
  await expect(sw()).toHaveAttribute("aria-checked", "false", { timeout: 10_000 });
  await sw().click();
  await expect(sw()).toHaveAttribute("aria-checked", "true");
  await expect.poll(async () => (await profileMods(page)).shroudforge).toEqual([expect.objectContaining({ id: "better-chests", enabled: true })]);
  await page.reload();
  await page.locator(".k-splash").waitFor({ state: "detached" });
  await expect(sw()).toHaveAttribute("aria-checked", "true", { timeout: 10_000 });
});

test("a mod that is not in the profile switches on, and then is in it", async ({ page }) => {
  await desktop(page);
  expect((await profileMods(page)).enshwick).toEqual([]);
  const sw = row(page, "Lanterns").getByRole("switch", { name: "Lanterns" });
  await expect(sw).toHaveAttribute("aria-checked", "false", { timeout: 10_000 });
  await sw.click();
  await expect(sw).toHaveAttribute("aria-checked", "true");
  await expect.poll(async () => (await profileMods(page)).enshwick).toEqual([expect.objectContaining({ id: "you.lanterns", enabled: true })]);
  await expect(say(page)).toHaveText(/^13 mods on, 14 off\./);
});

test("the list filters by name and by kind", async ({ page }) => {
  await desktop(page);
  await expect(row(page, "New Items")).toBeVisible({ timeout: 10_000 });
  await page.locator(".k-hero").getByLabel("Filter").fill("chest");
  await expect(row(page, "Better Chests")).toBeVisible();
  await expect(row(page, "chestsort.dll")).toBeVisible();
  await expect(row(page, "New Items")).toHaveCount(0);
  await page.getByLabel("Filter").fill("");
  await page.getByRole("combobox", { name: "Kind" }).selectOption({ label: "Native" });
  await expect(list(page).getByRole("listitem")).toHaveCount(4);
  await expect(row(page, "FPS Unlock")).toBeVisible();
});

test("All on and All off act on the rows shown, save the profile once, say what they left out, and Undo puts back what they changed", async ({ page }) => {
  await desktop(page);
  await expect(say(page)).toHaveText(/^12 mods on, 15 off\./, { timeout: 10_000 });
  const bar = page.locator(".k-hero .k-mods-bar");
  // the whole list: the counts are what would change
  await expect(bar.getByRole("button", { name: /^All on \(\d+\)$/ })).toBeEnabled();
  await expect(bar.getByRole("button", { name: /^All off \(\d+\)$/ })).toBeEnabled();
  // narrowed to one kind: only those
  await page.getByRole("combobox", { name: "Kind" }).selectOption({ label: "Native" });
  const before = await profileMods(page);
  const on = before.native.filter((m) => m.enabled).length;
  expect(on).toBeGreaterThan(1);
  const gen = (await api<{ generation: number }>(page, "profileGet", "ember-run")).generation;
  const off = bar.getByRole("button", { name: `Shown off (${on})` });
  await expect(off).toHaveAttribute("title", new RegExp(`^Switches off ${on} mods of the 4 the filter shows`));
  await off.click();
  const done = page.locator(".k-toast").filter({ hasText: `Switched ${on} mods off` });
  await expect(done).toBeVisible({ timeout: 5000 });
  await expect.poll(async () => (await profileMods(page)).native.filter((m) => m.enabled).length).toBe(0);
  // one save for all of them, and nothing of another kind changed
  expect((await api<{ generation: number }>(page, "profileGet", "ember-run")).generation).toBe(gen + 1);
  const after = await profileMods(page);
  expect({ ...after, native: [] }).toEqual({ ...before, native: [] });
  expect(after.native.map((m) => m.id)).toEqual(before.native.map((m) => m.id));
  await expect(bar.getByRole("button", { name: "Shown off (0)" })).toBeDisabled();
  // Undo: back as it was, in one more save
  await done.getByRole("button", { name: "Undo" }).click();
  await expect.poll(async () => (await profileMods(page)).native).toEqual(before.native);
  expect((await api<{ generation: number }>(page, "profileGet", "ember-run")).generation).toBe(gen + 2);
  // nothing was switched in between: Undo keeps nothing and says nothing about later changes (review finding 6)
  await expect(page.locator(".k-toast").filter({ hasText: "Undo kept later changes" })).toHaveCount(0);
  // a game-folder mod has no switch: All on leaves it out and says so
  await page.getByRole("combobox", { name: "Kind" }).selectOption({ label: "SET" });
  const setOn = bar.getByRole("button", { name: /^Shown on \(\d+\)$/ });
  await expect(setOn).toHaveAttribute("title", /can't be switched: in the game folder, it has no switch\.$/);
});

test("Undo of All on leaves a mod the player switched since, even back to the same state, and counts only that one", async ({ page }) => {
  await desktop(page);
  await expect(say(page)).toHaveText(/^12 mods on, 15 off\./, { timeout: 10_000 });
  const bar = page.locator(".k-hero .k-mods-bar");
  await page.getByRole("combobox", { name: "Kind" }).selectOption({ label: "Enshwick" });
  expect((await profileMods(page)).enshwick).toEqual([]);
  await bar.getByRole("button", { name: "Shown on (1)" }).click();
  const done = page.locator(".k-toast").filter({ hasText: "Switched 1 mod on" });
  await expect(done).toBeVisible({ timeout: 5000 });
  const sw = row(page, "Lanterns").getByRole("switch", { name: "Lanterns" });
  await expect(sw).toHaveAttribute("aria-checked", "true");
  // off and on again by hand: the entry is as the action left it, but the player chose it
  await sw.click();
  await expect(sw).toHaveAttribute("aria-checked", "false");
  await sw.click();
  await expect(sw).toHaveAttribute("aria-checked", "true");
  await expect.poll(async () => (await profileMods(page)).enshwick).toEqual([expect.objectContaining({ id: "you.lanterns", enabled: true })]);
  await done.getByRole("button", { name: "Undo" }).click();
  await expect(page.locator(".k-toast").filter({ hasText: "Undo kept later changes" })).toContainText("1 mod was switched since.");
  await expect(sw).toHaveAttribute("aria-checked", "true");
  expect((await profileMods(page)).enshwick).toEqual([expect.objectContaining({ id: "you.lanterns", enabled: true })]);
});

test("Undo of All off says it cannot when another profile was loaded since, and writes nothing", async ({ page }) => {
  await desktop(page);
  await expect(say(page)).toHaveText(/^12 mods on, 15 off\./, { timeout: 10_000 });
  await page.getByRole("combobox", { name: "Kind" }).selectOption({ label: "Native" });
  await page.locator(".k-hero .k-mods-bar").getByRole("button", { name: /^Shown off \(\d+\)$/ }).click();
  const done = page.locator(".k-toast").filter({ hasText: /^Switched \d+ mods off/ });
  await expect(done).toBeVisible({ timeout: 5000 });
  await expect.poll(async () => (await profileMods(page)).native.filter((m) => m.enabled).length).toBe(0);
  const ember = await api<{ generation: number; mods: Mods }>(page, "profileGet", "ember-run");
  const vanilla = await api<{ generation: number }>(page, "profileGet", "vanilla-plus");
  await page.evaluate(() => { location.hash = "#/settings"; });
  const sec = page.getByRole("region", { name: "Profiles" });
  await sec.getByRole("listitem").filter({ hasText: "Vanilla plus" }).getByRole("button", { name: "Load" }).click();
  await expect(sec.getByRole("listitem").filter({ hasText: "Vanilla plus" })).toContainText("Loaded", { timeout: 5000 });
  await page.evaluate(() => { location.hash = "#/"; });
  await expect(say(page)).toHaveText(/^4 mods on/, { timeout: 10_000 });
  await done.getByRole("button", { name: "Undo" }).click();
  await expect(page.locator(".k-toast").filter({ hasText: "Can't undo that" })).toContainText("Ember run is not the loaded profile now.");
  expect((await api<{ generation: number }>(page, "profileGet", "ember-run")).generation).toBe(ember.generation);
  expect((await api<{ generation: number }>(page, "profileGet", "vanilla-plus")).generation).toBe(vanilla.generation);
});

test("Undo of All off says it cannot when the profile changed elsewhere since", async ({ page }) => {
  await desktop(page);
  await expect(say(page)).toHaveText(/^12 mods on, 15 off\./, { timeout: 10_000 });
  await page.getByRole("combobox", { name: "Kind" }).selectOption({ label: "Native" });
  await page.locator(".k-hero .k-mods-bar").getByRole("button", { name: /^Shown off \(\d+\)$/ }).click();
  const done = page.locator(".k-toast").filter({ hasText: /^Switched \d+ mods off/ });
  await expect(done).toBeVisible({ timeout: 5000 });
  await expect.poll(async () => (await profileMods(page)).native.filter((m) => m.enabled).length).toBe(0);
  // another window saves Ember run (its notes)
  const p = await api<{ generation: number; notes: string }>(page, "profileGet", "ember-run");
  await api(page, "profileSave", { ...p, notes: "changed elsewhere" });
  // (the list has not read it again: the Undo asks the backend before it writes)
  await done.getByRole("button", { name: "Undo" }).click();
  await expect(page.locator(".k-toast").filter({ hasText: "Can't undo that" })).toContainText("Ember run changed elsewhere since.");
  const now = await api<{ generation: number; notes: string; mods: Mods }>(page, "profileGet", "ember-run");
  expect(now.generation).toBe(p.generation + 1);
  expect(now.notes).toBe("changed elsewhere");
  expect(now.mods.native.filter((m) => m.enabled)).toEqual([]);
});

// final review B2: an edit made on another page (Native and DLLs edits the profile through the shared save queue, not
// the list's switches) while the action's save is still running is the player's choice too: Undo leaves it
test("Undo of All off leaves a mod switched on another page while the save ran", async ({ page }) => {
  await desktop(page);
  await expect(say(page)).toHaveText(/^12 mods on, 15 off\./, { timeout: 10_000 });
  await page.getByRole("combobox", { name: "Kind" }).selectOption({ label: "Native" });
  const on = (await profileMods(page)).native.filter((m) => m.enabled).map((m) => m.id);
  expect(on).toContain("fps-unlock");
  type Plan = { __kilnMock: { planCommand: (c: string, p: unknown) => void } };
  await page.evaluate(() => (window as unknown as Plan).__kilnMock.planCommand("profile_save", { delay: 2500 }));
  await page.locator(".k-hero .k-mods-bar").getByRole("button", { name: /^Shown off \(\d+\)$/ }).click();
  const done = page.locator(".k-toast").filter({ hasText: /^Switched \d+ mods off/ });
  await expect(done).toBeVisible({ timeout: 5000 });
  // the save is still running: on Native and DLLs, FPS Unlock on and off again by hand
  await page.evaluate(() => { location.hash = "#/mods/native"; });
  const fps = page.locator("main#main").getByRole("button", { name: "Light FPS Unlock" });
  await expect(fps).toHaveAttribute("aria-pressed", "false", { timeout: 5000 });
  await fps.click();
  await expect(fps).toHaveAttribute("aria-pressed", "true");
  await fps.click();
  await expect(fps).toHaveAttribute("aria-pressed", "false");
  await page.evaluate(() => { location.hash = "#/"; });
  await expect.poll(async () => (await profileMods(page)).native.filter((m) => m.enabled).length, { timeout: 10_000 }).toBe(0);
  await page.waitForTimeout(300);
  await done.getByRole("button", { name: "Undo" }).click();
  await expect(page.locator(".k-toast").filter({ hasText: "Undo kept later changes" })).toContainText("1 mod was switched since.");
  await expect.poll(async () => (await profileMods(page)).native.filter((m) => m.enabled).map((m) => m.id).sort())
    .toEqual(on.filter((id) => id !== "fps-unlock").sort());
});

test("removing a mod is one click with a plain confirmation", async ({ page }) => {
  await desktop(page);
  await row(page, "Night Sky").getByRole("button", { name: "More for Night Sky" }).click({ timeout: 10_000 });
  await page.getByRole("menuitem", { name: "Remove", exact: true }).click();
  const dialog = page.getByRole("dialog", { name: "Remove Night Sky?" });
  await expect(dialog).toBeVisible();
  await expect(dialog.getByRole("textbox")).toHaveCount(0);
  await dialog.getByRole("button", { name: "Remove", exact: true }).click();
  await expect(dialog).toBeHidden();
  await expect(row(page, "Night Sky")).toHaveCount(0);
  expect((await api<{ id: string }[]>(page, "libraryList")).some((e) => e.id === "night-sky")).toBe(false);
  expect((await profileMods(page)).kiln.some((m) => m.id === "night-sky")).toBe(false);
});

test("Settings > Profiles saves the current set under a new name, loads another, and the main list follows", async ({ page }) => {
  await desktop(page, "/settings");
  const sec = page.getByRole("region", { name: "Profiles" });
  await expect(sec.getByRole("listitem").filter({ hasText: "Ember run" })).toContainText("Loaded");
  await sec.getByRole("button", { name: "Save current as…" }).click();
  const dialog = page.getByRole("dialog", { name: "Save current as" });
  await dialog.getByLabel("Name").fill("Night run");
  await dialog.getByRole("button", { name: "Save", exact: true }).click();
  await expect(sec.getByRole("listitem").filter({ hasText: "Night run" })).toBeVisible();
  const copy = await api<{ id: string; mods: Mods }>(page, "profileGet", "night-run");
  expect(copy.mods).toEqual(await profileMods(page));
  // every kind counts: Ember run holds 5 Kiln, 4 native, 1 EMF, 1 EML, 1 ShroudForge, 1 Greeneye, 1 Shroudtopia
  await expect(sec.getByRole("listitem").filter({ hasText: "Ember run" })).toContainText("14 mods");
  await sec.getByRole("listitem").filter({ hasText: "Vanilla plus" }).getByRole("button", { name: "Load" }).click();
  await expect(sec.getByRole("listitem").filter({ hasText: "Vanilla plus" })).toContainText("Loaded", { timeout: 5000 });
  await page.getByRole("link", { name: "Kiln: Play" }).click();
  // Vanilla plus: Weather Plus, and the game folder's Greeneye and SET mods its older drop-in rules load (4); off: the
  // 21 other Library mods and 3 game-folder mods
  await expect(say(page)).toHaveText(/^4 mods on, 24 off\./, { timeout: 10_000 });
  await expect(row(page, "Weather Plus").getByRole("switch", { name: "Weather Plus" })).toHaveAttribute("aria-checked", "true");
  await expect(row(page, "New Items").getByRole("switch", { name: "New Items" })).toHaveAttribute("aria-checked", "false");
  // another profile than the default is loaded: one small line says so and leads to Settings
  const line = page.locator(".k-hero").getByRole("link", { name: /Vanilla plus/ });
  await expect(line).toBeVisible();
  await line.click();
  await expect(page).toHaveURL(/#\/settings#profiles$/);
});

test("the header has no Profiles or Library; Play has no profile picker and no Another profile", async ({ page }) => {
  await desktop(page);
  const nav = page.getByRole("navigation", { name: "Main" });
  await expect(nav.getByRole("link", { name: "Profiles", exact: true })).toHaveCount(0);
  await expect(nav.getByRole("link", { name: "Library", exact: true })).toHaveCount(0);
  await expect(page.getByRole("button", { name: /Change profile/ })).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Another profile" })).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Without mods", exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "Vanilla", exact: true })).toBeVisible();
  // another profile than the default is loaded (the seeded Ember run): one small line
  await expect(page.locator(".k-hero").getByRole("link", { name: /Ember run/ })).toBeVisible({ timeout: 10_000 });
  // the default profile loaded: neither the word nor its name appears on the main screen
  await page.evaluate(() => sessionStorage.setItem("kiln.mock.persist", "1"));
  await page.reload();
  await page.locator(".k-splash").waitFor({ state: "detached" });
  await api(page, "profileCreate", "My mods", "ember-run");
  await api(page, "profileActivate", "my-mods", "client-steam");
  await page.reload();
  await page.locator(".k-splash").waitFor({ state: "detached" });
  await expect(say(page)).toHaveText(/^12 mods on/, { timeout: 10_000 });
  await api(page, "modpackDismiss", "client-steam", "srv-4b8e2c"); // the seeded refusal banner names a server, not a profile
  await expect(page.locator(".k-hero")).not.toContainText(/profile|My mods/i);
});

test("the old routes send the player to the new places", async ({ page }) => {
  // each from a fresh page load, as a bookmark or a link opens it
  let n = 0;
  const open = async (route: string) => {
    await page.goto(`/?load=${n++}#${route}`);
    await page.locator(".k-splash").waitFor({ state: "detached" });
  };
  await open("/profiles");
  await expect(page).toHaveURL(/#\/settings#profiles$/);
  await expect(page.getByRole("region", { name: "Profiles" })).toBeVisible();
  await open("/library");
  await expect(page).toHaveURL(/#\/$/);
  await open("/profiles/ember-run/native");
  await expect(page).toHaveURL(/#\/mods\/native$/);
  await open("/profiles/ember-run/engines");
  await expect(page).toHaveURL(/#\/mods\/engines$/);
  await open("/profiles/ember-run/configs");
  await expect(page).toHaveURL(/#\/mods\/configs$/);
  await open("/profiles/ember-run/mods");
  await expect(page).toHaveURL(/#\/$/);
  await open("/profiles/ember-run/saves");
  await expect(page).toHaveURL(/#\/saves$/);
  // a profile that is not the loaded one: Settings, where it can be loaded
  await open("/profiles/vanilla-plus/mods");
  await expect(page).toHaveURL(/#\/settings#profiles$/);
});

test("Load order and Engines are reached from the list", async ({ page }) => {
  await desktop(page);
  await page.locator(".k-hero").getByRole("link", { name: "Load order" }).click({ timeout: 10_000 });
  await expect(page).toHaveURL(/#\/mods\/order$/);
  const order = page.getByRole("list", { name: "Load order on a client" });
  await expect(order).toBeVisible();
  // every kind, not only Kiln's: the native phases, the other loaders' plugins with their mods, Kiln's resolved order,
  // the world, and the game folder's mods that load
  for (const [step, name] of [["In the proxy, before Kiln", "FPS Unlock"], ["As Kiln starts", "Creative Mode"], ["Kiln's plugins", "Beastmaster"], ["Kiln's plugins", "Better Chests"],
    ["Kiln's plugins", "Zoom Out"], ["Kiln's plugins", "Fly Mode"], ["At the main menu", "New Items"], ["At the main menu", "Weather Plus"], ["With your world", "HUD Tweaks"],
    ["From the game folder", "Fly Mode"]]) {
    await expect(order.locator(".k-order-step").filter({ hasText: step }).getByText(name, { exact: true }).first(), `${step}: ${name}`).toBeVisible();
  }
  await expect(page.getByRole("region", { name: "On, but not loading" })).toContainText("More Hotbars");
  await expect(page.getByText("10 mods load on a client. 2 on but not loading.")).toBeVisible();
  await page.getByRole("link", { name: "Native and DLLs" }).click();
  await expect(page.getByText("Dllmain", { exact: false }).first()).toBeVisible();
  await desktop(page);
  await page.locator(".k-hero").getByRole("link", { name: "Engines" }).click({ timeout: 10_000 });
  await expect(page).toHaveURL(/#\/mods\/engines$/);
  await expect(page.locator('[aria-label="Engine plugins"]')).toBeVisible();
});

test("the Deck's Mods screen lists every kind with a switch and counts them all", async ({ page }) => {
  await page.goto("/?deck=steamdeck#/deck/mods");
  const mods = page.getByRole("list", { name: "Mods" });
  await expect(page.getByText(/12 mods on, 15 off/)).toBeVisible({ timeout: 10_000 });
  for (const id of ["mod-kiln-new-items", "mod-shroudforge-better-chests", "mod-eml-loot-tables-x", "mod-enshwick-you.lanterns", "mod-ember-private-chests", "found-mods/HoardersHelper"])
    await expect(mods.locator(`[data-deck-id="${id}"]`), id).toHaveCount(1);
  // A switches a mod the profile lacks on
  await mods.locator('[data-deck-id="mod-enshwick-you.lanterns"]').click();
  await expect(page.getByText(/13 mods on, 14 off/)).toBeVisible();
  // no profile picker in the main flow: Home names no profile switch, and its count covers every kind
  await page.goto("/?deck=steamdeck#/deck");
  await expect(page.getByTestId("deck-mods")).toContainText("13 mods on, 14 off");
  await expect(page.locator('[data-deck-id="switch-profile"]')).toHaveCount(0);
  await expect(page.locator('.deck-hint[data-hint="y"]')).toHaveCount(0);
});

test("a right click on a row opens its menu where the pointer is", async ({ page }) => {
  await desktop(page);
  const r = row(page, "Better Chests");
  await r.click({ button: "right", position: { x: 160, y: 12 }, timeout: 10_000 });
  const menu = page.getByRole("menu");
  await expect(menu).toBeVisible();
  for (const item of ["Switch off", "Settings", "Open folder", "Remove"]) await expect(menu.getByRole("menuitem", { name: item, exact: true })).toBeVisible();
  const box = await r.boundingBox();
  const at = await menu.boundingBox();
  expect(Math.abs(at!.x - (box!.x + 160))).toBeLessThan(4); // at the pointer, not at the row's More button
  await menu.getByRole("menuitem", { name: "Switch off", exact: true }).click();
  await expect(r.getByRole("switch", { name: "Better Chests" })).toHaveAttribute("aria-checked", "false");
  // a game-folder row: its fix and its folder
  await row(page, "Hoarder's Helper").click({ button: "right" });
  await expect(page.getByRole("menuitem", { name: "Load the game-folder EML mods" })).toBeVisible();
  await page.keyboard.press("Escape");
  // Settings, Profiles: a profile's row
  await desktop(page, "/settings");
  await page.getByRole("region", { name: "Profiles" }).getByRole("listitem").filter({ hasText: "Vanilla plus" }).click({ button: "right" });
  await expect(page.getByRole("menuitem", { name: "Rename" })).toBeVisible();
  // a field keeps the webview's own menu (copy, paste): nothing of Kiln's opens
  await page.keyboard.press("Escape");
  await desktop(page);
  await page.locator(".k-hero").getByLabel("Filter").click({ button: "right" });
  await expect(page.getByRole("menu")).toHaveCount(0);
});

test("a mod with several Library versions: its menu picks one and removes another (what the Library page did)", async ({ page }) => {
  await desktop(page);
  const dt = await page.evaluateHandle(() => { const d = new DataTransfer(); d.items.add(new File(["x"], "more-hotbars-2.2.0.zip")); return d; });
  await page.locator(".k-hero").dispatchEvent("dragover", { dataTransfer: dt });
  await page.locator(".k-hero").dispatchEvent("drop", { dataTransfer: dt });
  const r = row(page, "More Hotbars");
  await expect(r).toContainText("2.2.0", { timeout: 10_000 }); // the dropped version is the one switched on
  await r.getByRole("button", { name: "More for More Hotbars" }).click();
  await page.getByRole("menuitem", { name: "Use 2.1.0" }).click();
  await expect(r).toContainText("2.1.0");
  await expect.poll(async () => (await profileMods(page)).kiln.find((m) => m.id === "more-hotbars")?.version).toBe("2.1.0");
  await r.getByRole("button", { name: "More for More Hotbars" }).click();
  await expect(page.getByRole("menuitem", { name: "Remove 2.1.0" })).toHaveCount(0); // not the version in use
  await page.getByRole("menuitem", { name: "Remove 2.2.0" }).click();
  const dialog = page.getByRole("dialog", { name: "Remove More Hotbars 2.2.0?" });
  await dialog.getByRole("button", { name: "Remove", exact: true }).click();
  await expect(dialog).toBeHidden();
  await expect.poll(async () => (await api<{ id: string; versions: { version: string }[] }[]>(page, "libraryList")).find((e) => e.id === "more-hotbars")?.versions.map((v) => v.version)).toEqual(["2.1.0"]);
  await expect(r.getByRole("switch", { name: "More Hotbars" })).toHaveAttribute("aria-checked", "true");
});

// The player's report (dev.25): every mod switch raised the same "your own folder" notes again, a wall of text. Each
// sentence is raised once per game now; Notes above the list reads them again; Settings can turn them off.
test("switching mods raises a folder note once, Notes reads it again, and the setting turns the notes off", async ({ page }) => {
  type Mock = { __kilnMock: { planActivation: (r: unknown) => void } };
  const plan = (r: unknown) => page.evaluate((r) => (window as unknown as Mock).__kilnMock.planActivation(r), r);
  const OWN = "shroudforge:better-chests: the game uses your own folder /games/Enshrouded/mods/BetterChests for this mod; Kiln leaves it as it is and places no copy of its own.";
  const NEW = "shroudforge:better-chests: a note the earlier activations did not have.";
  await desktop(page);
  const sw = () => row(page, "Better Chests").getByRole("switch", { name: "Better Chests" });
  await expect(sw()).toHaveAttribute("aria-checked", "true", { timeout: 10_000 });
  const notes = page.locator(".k-toast", { hasText: "Notes on your mods" });
  await plan({ warnings: [OWN] });
  await sw().click();
  await expect(notes).toContainText(OWN);
  await notes.getByRole("button", { name: "Dismiss", exact: true }).click();
  // the next switch says the same and one new sentence: only the new one comes
  await plan({ warnings: [OWN, NEW] });
  await sw().click();
  await expect(notes).toContainText(NEW);
  await expect(notes).not.toContainText(OWN);
  await notes.getByRole("button", { name: "Dismiss", exact: true }).click();
  // both are in Notes above the list
  await page.locator(".k-hero").getByRole("button", { name: "Notes (2)" }).click();
  const dialog = page.getByRole("dialog", { name: "Notes about your mod folders" });
  await expect(dialog).toContainText(OWN);
  await expect(dialog).toContainText(NEW);
  await dialog.getByRole("button", { name: "Done" }).click();
  // off in Settings: a new note is not raised, the first notice of a folder that may still run a mod that is off is
  await page.evaluate(() => { location.hash = "#/settings"; });
  await page.getByText("Show notes about mod folders", { exact: true }).click();
  await expect(page.getByRole("switch", { name: /^Show notes about mod folders/ })).not.toBeChecked();
  await expect.poll(() => page.evaluate(async () => (await (await import("/src/api/commands.ts")).appSettingsGet()).modFolderNotes)).toBe(false);
  await page.evaluate(() => { location.hash = "#/"; });
  await expect(sw()).toBeVisible({ timeout: 10_000 });
  await plan({ warnings: [OWN, NEW, "shroudforge:better-chests: a third note."], retained: [{ path: "mods/edited", reason: "files Kiln placed were changed. It is off in the profile, but ShroudForge may still load it.", mayRun: true, off: true }] });
  await sw().click();
  await expect(page.locator(".k-toast", { hasText: "Some mod folders were kept" })).toContainText("mods/edited");
  await expect(notes).toHaveCount(0);
  await expect(page.locator(".k-hero").getByRole("button", { name: "Notes (4)" })).toBeVisible();
});

// A ShroudForge mod the player's own folder serves: its settings are editable, and the dialog says that Kiln changes
// only that setting there (engine_mods::set_setting).
test("ShroudForge settings in the player's own folder can be edited and say whose folder it is", async ({ page }) => {
  await desktop(page);
  await page.evaluate(() => (window as unknown as { __kilnMock: { setSfOwnFolder: (id: string) => void } }).__kilnMock.setSfOwnFolder("better-chests"));
  await expect(row(page, "Better Chests")).toBeVisible({ timeout: 10_000 });
  await row(page, "Better Chests").getByLabel("More for Better Chests").click();
  await page.getByRole("menuitem", { name: "Settings", exact: true }).click();
  const dialog = page.getByRole("dialog", { name: "Better Chests settings" });
  await expect(dialog.locator("[data-own-folder]")).toHaveText("This is your own folder, not a copy Kiln placed: Kiln changes only the setting you change here.");
  const sort = dialog.getByRole("switch", { name: "autoSort" });
  await expect(sort).toBeEnabled();
  await sort.dispatchEvent("click");
  await expect(sort).toBeChecked();
});

// "Let Kiln manage this folder" (dev.27): a ShroudForge folder the player put into the game serves a mod, and Kiln
// leaves it alone. The Notes dialog lists it with a button; the button asks first and says what changes; after it the
// folder is Kiln's and the own-folder note is gone. It is never done without the player's click.
test("the player's own folder can be handed to Kiln from Notes, after a confirmation that says what changes", async ({ page }) => {
  type Mock = { __kilnMock: { setOwnFolder: (id: string, own: unknown) => void; calls: () => string[] } };
  const OWN = "/games/Enshrouded/mods/BetterChests";
  const NOTE = `shroudforge:better-chests: the game uses your own folder ${OWN} for this mod; Kiln leaves it as it is and places no copy of its own.`;
  await desktop(page);
  const sw = () => row(page, "Better Chests").getByRole("switch", { name: "Better Chests" });
  await expect(sw()).toHaveAttribute("aria-checked", "true", { timeout: 10_000 });
  await page.evaluate((own) => (window as unknown as Mock).__kilnMock.setOwnFolder("better-chests", { path: own, renameTo: "/games/Enshrouded/mods/better-chests" }), OWN);
  // an activation says the note; nothing was taken over by it
  await api(page, "profileActivate", "ember-run", "client-steam");
  const adoptCalls = () => page.evaluate(() => (window as unknown as Mock).__kilnMock.calls().filter((c) => c === "engine_adopt_folder").length);
  expect(await adoptCalls()).toBe(0);
  // Notes lists the folder with the button
  await page.locator(".k-hero").getByRole("button", { name: /^Notes \(\d+\)$/ }).click();
  const dialog = page.getByRole("dialog", { name: "Notes about your mod folders" });
  await expect(dialog).toContainText(NOTE);
  const own = dialog.locator("[data-own-folder-row='shroudforge/better-chests']");
  await expect(own).toContainText(OWN);
  await expect(own).toContainText("Kiln neither switches, updates nor removes it.");
  await own.getByRole("button", { name: "Let Kiln manage this folder" }).click();
  // the confirmation: what changes, the rename, and that nothing else in the folder is touched now
  const confirm = page.getByRole("dialog", { name: "Let Kiln manage this folder?" });
  await expect(confirm).toContainText(OWN);
  await expect(confirm).toContainText("Kiln will update, switch and remove this folder with the mod from now on.");
  await expect(confirm).toContainText("It renames the folder to better-chests");
  await expect(confirm).toContainText("Nothing in the folder is changed now, except one small file Kiln adds");
  // Cancel does nothing
  await confirm.getByRole("button", { name: "Cancel" }).click();
  await expect(confirm).toHaveCount(0);
  expect(await adoptCalls()).toBe(0);
  await own.getByRole("button", { name: "Let Kiln manage this folder" }).click();
  await page.getByRole("dialog", { name: "Let Kiln manage this folder?" }).getByRole("button", { name: "Let Kiln manage it" }).click();
  await expect(page.locator(".k-toast", { hasText: "Kiln manages this folder now" })).toContainText("/games/Enshrouded/mods/better-chests (it was BetterChests)");
  expect(await adoptCalls()).toBe(1);
  // the folder is Kiln's: no row, and the mods were written to the game again, without that note
  await expect(dialog.locator("[data-own-folder-row]")).toHaveCount(0);
  await expect(dialog).not.toContainText(NOTE);
  expect(await api<unknown[]>(page, "engineOwnFolders", "ember-run", "client-steam")).toEqual([]);
});

test("a folder that is not exactly the mod's is not taken over, and the reason names the file", async ({ page }) => {
  type Mock = { __kilnMock: { setOwnFolder: (id: string, own: unknown) => void; planCommand: (cmd: string, plan: unknown) => void } };
  const OWN = "/games/Enshrouded/mods/BetterChests";
  const WHY = "Kiln cannot take this folder over: src/mod.lua is not the mod's file (its content differs). It takes a folder over only when every file in it is the file of better-chests 0.4.1 in your Library (the values of your settings may differ).";
  await desktop(page);
  const sw = () => row(page, "Better Chests").getByRole("switch", { name: "Better Chests" });
  await expect(sw()).toHaveAttribute("aria-checked", "true", { timeout: 10_000 });
  await page.evaluate(([own, why]) => (window as unknown as Mock).__kilnMock.setOwnFolder("better-chests", { path: own, refusal: why }), [OWN, WHY]);
  await api(page, "profileActivate", "ember-run", "client-steam");
  await page.locator(".k-hero").getByRole("button", { name: /^Notes \(\d+\)$/ }).click();
  const dialog = page.getByRole("dialog", { name: "Notes about your mod folders" });
  const own = dialog.locator("[data-own-folder-row='shroudforge/better-chests']");
  // the reason stands where the button would be
  await expect(own.locator("[data-own-folder-refusal]")).toHaveText(WHY);
  await expect(own.getByRole("button", { name: "Let Kiln manage this folder" })).toHaveCount(0);
  // the folder changed between the list and the click: the backend's refusal is shown as it is, and the row stays
  await page.evaluate((own) => (window as unknown as Mock).__kilnMock.setOwnFolder("better-chests", { path: own }), OWN);
  await dialog.getByRole("button", { name: "Done" }).click();
  await page.locator(".k-hero").getByRole("button", { name: /^Notes \(\d+\)$/ }).click();
  await expect(own.getByRole("button", { name: "Let Kiln manage this folder" })).toBeVisible();
  await page.evaluate(([own, why]) => (window as unknown as Mock).__kilnMock.setOwnFolder("better-chests", { path: own, refusal: why }), [OWN, WHY]);
  await own.getByRole("button", { name: "Let Kiln manage this folder" }).click();
  await page.getByRole("dialog", { name: "Let Kiln manage this folder?" }).getByRole("button", { name: "Let Kiln manage it" }).click();
  const refused = page.locator(".k-toast", { hasText: "Kiln did not take the folder over" });
  await expect(refused).toContainText(`${OWN}: ${WHY}`);
  await expect(own.locator("[data-own-folder-refusal]")).toHaveText(WHY);
  expect((await api<{ refusal?: string }[]>(page, "engineOwnFolders", "ember-run", "client-steam"))[0].refusal).toBe(WHY);
});

test("the mod's settings offer the same button for the player's own folder, and not while the game runs", async ({ page }) => {
  type Mock = { __kilnMock: { setOwnFolder: (id: string, own: unknown) => void; startOutside: (ago: number) => void; stopGame: () => void } };
  await desktop(page);
  await page.evaluate(() => (window as unknown as Mock).__kilnMock.setOwnFolder("better-chests", { path: "/games/Enshrouded/mods/better-chests" }));
  await expect(row(page, "Better Chests")).toBeVisible({ timeout: 10_000 });
  await row(page, "Better Chests").getByLabel("More for Better Chests").click();
  await page.getByRole("menuitem", { name: "Settings", exact: true }).click();
  const dialog = page.getByRole("dialog", { name: "Better Chests settings" });
  await expect(dialog.locator("[data-own-folder]")).toBeVisible();
  const button = dialog.getByRole("button", { name: "Let Kiln manage this folder" });
  await expect(button).toBeEnabled();
  // a running game reads the folder: not now
  await page.evaluate(() => (window as unknown as Mock).__kilnMock.startOutside(5));
  await expect(button).toBeDisabled();
  await expect(button).toHaveAttribute("title", "Close the game first");
  await page.evaluate(() => (window as unknown as Mock).__kilnMock.stopGame());
  await expect(button).toBeEnabled();
  await button.click();
  const confirm = page.getByRole("dialog", { name: "Let Kiln manage this folder?" });
  await expect(confirm).toContainText("Kiln will update, switch and remove this folder with the mod from now on.");
  await expect(confirm).not.toContainText("It renames the folder");
  await confirm.getByRole("button", { name: "Let Kiln manage it" }).click();
  await expect(page.locator(".k-toast", { hasText: "Kiln manages this folder now" })).toBeVisible();
  // Kiln's copy now: the settings no longer say it is the player's folder, and the button is gone
  await expect(dialog.locator("[data-own-folder]")).toHaveCount(0);
  await expect(dialog.getByRole("button", { name: "Let Kiln manage this folder" })).toHaveCount(0);
});
