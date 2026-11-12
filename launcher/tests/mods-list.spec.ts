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
