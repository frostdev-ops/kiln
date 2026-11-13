// Several mods at once in the main list (src/shell/selection.ts, MainList.tsx, deck/screens/Mods.tsx): a box per row,
// Shift+click for a range, Ctrl+A for every row shown, Escape for none, Space on a box; the bar's Switch on, Switch off
// (with Undo), Remove… (one confirmation naming every mod and who else lists it, one profile save, then the Library
// past exactly the users it showed; a refusal leaves that mod with the reason); the selection outlives a start that
// hides the list; the Deck selects with L2 and acts with R2. Browser mock only (src/api/mock.ts): "Ember run" loaded.
// Condition waits only. Screenshots: SELECT_SHOTS (a folder; none when unset).
import { expect, test, type Page } from "@playwright/test";
import { mkdirSync } from "node:fs";
import { join } from "node:path";

const SHOTS = process.env.SELECT_SHOTS;
if (SHOTS) mkdirSync(SHOTS, { recursive: true });
const shot = async (page: Page, name: string, el?: ReturnType<Page["locator"]>) => {
  if (!SHOTS) return;
  if (el) await el.screenshot({ path: join(SHOTS, `${name}.png`) });
  else await page.screenshot({ path: join(SHOTS, `${name}.png`) });
};

type Mods = Record<string, { id: string; enabled: boolean }[]>;
type Mock = { __kilnMock: { planCommand: (c: string, p: unknown) => void; openGate: (g: string) => void; stopGame: () => void; scriptStart: (o: unknown) => void;
  emit: (n: string, v: unknown) => void } };

async function desktop(page: Page, route = "/") {
  await page.goto(`/#${route}`);
  await page.locator(".k-splash").waitFor({ state: "detached" });
  await expect(page.locator(".k-hero .k-hero-say")).toHaveText(/^12 mods on, 15 off\./, { timeout: 10_000 });
}
const api = <T,>(page: Page, fn: string, ...args: unknown[]) =>
  page.evaluate(async ([f, a]) => {
    const m = (await import("/src/api/commands.ts")) as unknown as Record<string, (...x: unknown[]) => Promise<unknown>>;
    return m[f as string](...(a as unknown[]));
  }, [fn, args] as const) as Promise<T>;
const profileMods = async (page: Page) => (await api<{ mods: Mods }>(page, "profileGet", "ember-run")).mods;
const libraryIds = async (page: Page) => (await api<{ id: string }[]>(page, "libraryList")).map((e) => e.id);
const list = (page: Page) => page.locator(".k-hero").getByRole("list", { name: "Mods" });
const row = (page: Page, name: string) => list(page).getByRole("listitem").filter({ has: page.getByText(name, { exact: true }) });
const box = (page: Page, name: string) => list(page).getByRole("checkbox", { name: `Select ${name}` });
const selbar = (page: Page) => page.getByRole("toolbar", { name: "Selected mods" });
const kind = (page: Page, label: string) => page.getByRole("combobox", { name: "Kind" }).selectOption({ label });
/** The checked boxes' names, in the order shown. */
const checked = (page: Page) => list(page).locator('[role="checkbox"][aria-checked="true"]').evaluateAll((els) => els.map((e) => (e.getAttribute("aria-label") ?? "").replace(/^Select /, "")));
const kilnNames = ["Big Stacks", "More Hotbars", "New Items"];

test("a click, then Shift+click selects a range; Remove… names them in one confirmation; all three leave the list, the Library and the profile", async ({ page }) => {
  await desktop(page);
  await kind(page, "Kiln");
  await expect(box(page, "Big Stacks")).toBeVisible();
  await box(page, "Big Stacks").click();
  await box(page, "New Items").click({ modifiers: ["Shift"] });
  await expect.poll(() => checked(page)).toEqual(kilnNames);
  await expect(selbar(page)).toContainText("3 selected");
  await shot(page, "desktop-bar", page.locator(".k-hero .k-mods"));
  await selbar(page).getByRole("button", { name: "Remove…" }).click();
  const dlg = page.getByRole("dialog", { name: "Remove 3 mods?" });
  const names = dlg.getByRole("list", { name: "Mods to remove" });
  for (const n of kilnNames) await expect(names).toContainText(n);
  const go = dlg.getByRole("button", { name: "Remove 3" });
  await expect(go).toBeEnabled({ timeout: 10_000 });
  await expect(dlg).toContainText("Their files leave the Library.");
  await shot(page, "desktop-confirm", dlg);
  const before = (await api<{ generation: number }>(page, "profileGet", "ember-run")).generation;
  await go.click();
  await expect(page.locator(".k-toast").filter({ hasText: "Removed 3 mods" })).toBeVisible({ timeout: 10_000 });
  for (const n of kilnNames) await expect(row(page, n)).toHaveCount(0);
  await expect(selbar(page)).toHaveCount(0);
  const lib = await libraryIds(page);
  for (const id of ["big-stacks", "more-hotbars", "new-items"]) expect(lib).not.toContain(id);
  const p = await api<{ generation: number; mods: Mods }>(page, "profileGet", "ember-run");
  expect(p.mods.kiln.map((m) => m.id).filter((id) => ["big-stacks", "more-hotbars", "new-items"].includes(id))).toEqual([]);
  expect(p.generation).toBe(before + 1); // one save drops all three
});

test("with the typed-word option on, Remove of several asks for their number", async ({ page }) => {
  await page.addInitScript(() => localStorage.setItem("kiln.confirm.typeNames", "1"));
  await desktop(page);
  await kind(page, "Kiln");
  await box(page, "Big Stacks").click();
  await box(page, "More Hotbars").click();
  await selbar(page).getByRole("button", { name: "Remove…" }).click();
  const dlg = page.getByRole("dialog", { name: "Remove 2 mods?" });
  await expect(dlg.getByText("Type 2 to confirm")).toBeVisible();
  const go = dlg.getByRole("button", { name: "Remove 2" });
  await expect(go).toBeDisabled();
  await dlg.getByRole("textbox").fill("2");
  await expect(go).toBeEnabled({ timeout: 10_000 });
  await go.click();
  await expect(page.locator(".k-toast").filter({ hasText: "Removed 2 mods" })).toBeVisible({ timeout: 10_000 });
});

test("a mod a running server uses is refused: the others go, it stays in the Library with the reason", async ({ page }) => {
  await desktop(page);
  await kind(page, "Kiln");
  for (const n of kilnNames) await box(page, n).click();
  // the backend refuses the first removal (library_remove: a server whose profile file points into the Library)
  const why = "The server Ember Isles (lab) runs with a profile that uses it: stop it first.";
  await page.evaluate((m) => (window as unknown as Mock).__kilnMock.planCommand("library_remove", { error: { code: "game_running", message: m } }), why);
  await selbar(page).getByRole("button", { name: "Remove…" }).click();
  const dlg = page.getByRole("dialog", { name: "Remove 3 mods?" });
  await expect(dlg.getByRole("button", { name: "Remove 3" })).toBeEnabled({ timeout: 10_000 });
  await dlg.getByRole("button", { name: "Remove 3" }).click();
  const told = page.locator(".k-toast").filter({ hasText: "Removed 2, 1 left" });
  await expect(told).toContainText(`Big Stacks: ${why}`, { timeout: 10_000 });
  await expect(row(page, "Big Stacks")).toHaveCount(1);
  await expect(row(page, "More Hotbars")).toHaveCount(0);
  await expect(row(page, "New Items")).toHaveCount(0);
  const lib = await libraryIds(page);
  expect(lib).toContain("big-stacks");
  expect(lib).not.toContain("new-items");
});

test("Switch off for a selection, with Undo; Escape clears it", async ({ page }) => {
  await desktop(page);
  await kind(page, "Native");
  const on = (await profileMods(page)).native.filter((m) => m.enabled).map((m) => m.id);
  await box(page, "FPS Unlock").click();
  await box(page, "Creative Mode").click();
  await selbar(page).getByRole("button", { name: "Switch off" }).click();
  const done = page.locator(".k-toast").filter({ hasText: "Switched 2 mods off" });
  await expect(done).toBeVisible();
  await expect.poll(async () => (await profileMods(page)).native.filter((m) => m.enabled).map((m) => m.id).sort()).toEqual(on.filter((id) => !["fps-unlock", "creative-mode"].includes(id)).sort());
  await expect.poll(async () => page.evaluate(async () => (await import("/src/pages/profiles/useProfileEdit.ts")).profileQueueState("ember-run").idle)).toBe(true);
  await done.getByRole("button", { name: "Undo" }).click();
  await expect.poll(async () => (await profileMods(page)).native.filter((m) => m.enabled).map((m) => m.id).sort()).toEqual([...on].sort());
  // still selected after the switch; Escape lets go
  await box(page, "FPS Unlock").focus();
  await page.keyboard.press("Escape");
  await expect(selbar(page)).toHaveCount(0);
  await expect.poll(() => checked(page)).toEqual([]);
});

test("keyboard only: Tab to a box, Space selects it, Shift+Space a range, Ctrl+A every row shown, Escape none", async ({ page }) => {
  await desktop(page);
  await kind(page, "Kiln");
  // from the Kind field, Tab until a row's box has the focus
  await page.getByRole("combobox", { name: "Kind" }).focus();
  const onBox = () => page.evaluate(() => document.activeElement?.getAttribute("role") === "checkbox" ? document.activeElement.getAttribute("aria-label") : null);
  await expect.poll(async () => { if (await onBox()) return onBox(); await page.keyboard.press("Tab"); return onBox(); }, { timeout: 15_000 }).toBe("Select Big Stacks");
  await page.keyboard.press("Space");
  await expect.poll(() => checked(page)).toEqual(["Big Stacks"]);
  // to the next row's box (switch, More), Shift+Space: the range
  await expect.poll(async () => { await page.keyboard.press("Tab"); return onBox(); }, { timeout: 15_000 }).toBe("Select More Hotbars");
  await expect.poll(async () => { if ((await onBox()) === "Select New Items") return onBox(); await page.keyboard.press("Tab"); return onBox(); }, { timeout: 15_000 }).toBe("Select New Items");
  await page.keyboard.press("Shift+Space");
  await expect.poll(() => checked(page)).toEqual(kilnNames);
  await page.keyboard.press("Control+a");
  const shownKiln = await list(page).getByRole("checkbox").count();
  await expect.poll(async () => (await checked(page)).length).toBe(shownKiln);
  await expect(selbar(page)).toContainText(`${shownKiln} selected`);
  await page.keyboard.press("Escape");
  await expect.poll(() => checked(page)).toEqual([]);
});

test("a filter that hides selected rows lets go of them and says how many", async ({ page }) => {
  await desktop(page);
  await kind(page, "Kiln");
  await box(page, "Big Stacks").click();
  await box(page, "New Items").click();
  await page.locator(".k-hero").getByLabel("Filter").fill("stacks");
  await expect.poll(() => checked(page)).toEqual(["Big Stacks"]);
  await expect(selbar(page)).toContainText("1 selected");
  await expect(selbar(page)).toContainText("1 not shown any more left the selection");
});

test("the selection outlives a start that hides the list, and comes back with it", async ({ page }) => {
  await desktop(page);
  await box(page, "Big Stacks").click();
  await box(page, "Night Sky").click();
  await page.evaluate(() => (window as unknown as Mock).__kilnMock.scriptStart({ scale: 0.05 }));
  await page.locator(".k-door").click();
  await expect(page.getByRole("progressbar", { name: "Starting the game" })).toBeVisible({ timeout: 10_000 });
  await expect(list(page)).toHaveCount(0);
  await expect(page.locator(".k-head-state")).toHaveText(/^Running/, { timeout: 30_000 });
  await page.evaluate(() => (window as unknown as Mock).__kilnMock.stopGame());
  await expect(page.locator(".k-head-state")).toHaveText("Stopped");
  await expect.poll(() => checked(page), { timeout: 15_000 }).toEqual(["Big Stacks", "Night Sky"]);
  await expect(selbar(page)).toContainText("2 selected");
});

test("the Deck: L2 selects the focused mod, R2 opens what to do with the selected ones; Remove asks once, names them, and removes them", async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  const pad = async (button: string) => {
    for (const pressed of [true, false]) await page.evaluate(([b, p]) => (window as unknown as Mock).__kilnMock.emit("kiln://gamepad", { kind: "button", button: b, pressed: p, repeat: false, device: "event9" }), [button, pressed] as const);
  };
  await page.goto("/?deck=steamdeck#/deck/mods");
  await expect(page.getByText(/12 mods on, 15 off/)).toBeVisible({ timeout: 10_000 });
  const ids = ["mod-kiln-big-stacks", "mod-kiln-more-hotbars", "mod-kiln-new-items"];
  for (const id of ids) {
    const r = page.locator(`[data-deck-id="${id}"]`);
    await r.focus();
    await expect(page.locator('.deck-hint[data-hint="lt"]')).toContainText("Select");
    await pad("lt");
    await expect(r).toHaveAttribute("aria-label", /, selected$/);
  }
  await expect(page.locator('.deck-hint[data-hint="rt"]')).toContainText("3 selected");
  await shot(page, "deck-list");
  await pad("rt");
  const menu = page.locator('[data-deck-dialog="selected"]');
  await expect(menu).toBeVisible();
  await shot(page, "deck-selected");
  await menu.locator('[data-deck-id="sel-remove"]').focus();
  await pad("a");
  const dlg = page.locator('[data-deck-dialog="remove-many"]');
  await expect(dlg).toBeVisible();
  for (const n of kilnNames) await expect(dlg.getByRole("list", { name: "Mods to remove" })).toContainText(n);
  const go = dlg.locator('[data-deck-id="remove-many-confirm"]');
  await expect(go).toBeEnabled({ timeout: 10_000 });
  await shot(page, "deck-confirm");
  await go.focus();
  await pad("a");
  for (const id of ids) await expect(page.locator(`[data-deck-id="${id}"]`)).toHaveCount(0, { timeout: 10_000 });
  const lib = await libraryIds(page);
  for (const id of ["big-stacks", "more-hotbars", "new-items"]) expect(lib).not.toContain(id);
  await expect(page.locator('.deck-hint[data-hint="rt"]')).toHaveCount(0);
});

// review of 9aeffac9, P2: the selection belongs to one loaded profile of one game
test("a selection made in one profile is gone once another is loaded; Switch off acts on the new one only", async ({ page }) => {
  await desktop(page);
  await kind(page, "Kiln");
  await box(page, "Big Stacks").click();
  await box(page, "New Items").click();
  await expect(selbar(page)).toContainText("2 selected");
  await page.evaluate(() => { location.hash = "#/settings"; });
  const sec = page.getByRole("region", { name: "Profiles" });
  await sec.getByRole("listitem").filter({ hasText: "Vanilla plus" }).getByRole("button", { name: "Load" }).click();
  await expect(sec.getByRole("listitem").filter({ hasText: "Vanilla plus" })).toContainText("Loaded", { timeout: 10_000 });
  await page.evaluate(() => { location.hash = "#/"; });
  await expect(page.locator(".k-hero .k-hero-say")).toHaveText(/^4 mods on/, { timeout: 10_000 });
  await expect.poll(() => checked(page)).toEqual([]);
  await expect(selbar(page)).toHaveCount(0);
  const before = (await api<{ mods: Mods }>(page, "profileGet", "vanilla-plus")).mods;
  await box(page, "Weather Plus").click();
  await expect(selbar(page)).toContainText("1 selected");
  await selbar(page).getByRole("button", { name: "Switch off" }).click();
  await expect(page.locator(".k-toast").filter({ hasText: "Switched 1 mod off" })).toBeVisible();
  await expect.poll(async () => (await api<{ mods: Mods }>(page, "profileGet", "vanilla-plus")).mods.kiln)
    .toEqual(before.kiln.map((m) => (m.id === "weather-plus" ? { ...m, enabled: false } : m)));
  // back to Ember run: what was selected there before is not there any more either
  await page.evaluate(async () => (await import("/src/api/commands.ts")).profileActivate("ember-run", "client-steam"));
  await expect(page.locator(".k-hero .k-hero-say")).toHaveText(/mods on/, { timeout: 10_000 });
  await expect(box(page, "Big Stacks")).toBeVisible();
  await expect.poll(() => checked(page)).toEqual([]);
});

test("the Deck's selection ends when another profile is loaded", async ({ page }) => {
  const pad = async (button: string) => {
    for (const pressed of [true, false]) await page.evaluate(([b, p]) => (window as unknown as Mock).__kilnMock.emit("kiln://gamepad", { kind: "button", button: b, pressed: p, repeat: false, device: "event9" }), [button, pressed] as const);
  };
  await page.goto("/?deck=steamdeck#/deck/mods");
  await expect(page.getByText(/12 mods on, 15 off/)).toBeVisible({ timeout: 10_000 });
  const r = page.locator('[data-deck-id="mod-kiln-new-items"]');
  await r.focus();
  await pad("lt");
  await expect(r).toHaveAttribute("aria-label", /, selected$/);
  await expect(page.locator('.deck-hint[data-hint="rt"]')).toContainText("1 selected");
  await page.evaluate(async () => (await import("/src/api/commands.ts")).profileActivate("vanilla-plus", "client-steam"));
  await expect(page.getByText(/^4 mods on/)).toBeVisible({ timeout: 10_000 });
  await expect(page.locator('[data-deck-id="mod-kiln-new-items"]')).not.toHaveAttribute("aria-label", /, selected$/);
  await expect(page.locator('.deck-hint[data-hint="rt"]')).toHaveCount(0);
});

// review of 9aeffac9, P2: the list's shortcuts act only on keys pressed on the list itself
test("Escape and Ctrl+A in an open row menu, the filter or a dialog leave the selection alone; on the list they still work", async ({ page }) => {
  await desktop(page);
  await kind(page, "Kiln");
  await box(page, "Big Stacks").click();
  await expect.poll(() => checked(page)).toEqual(["Big Stacks"]);
  const menu = page.getByRole("menu");
  const more = row(page, "New Items").getByRole("button", { name: "More for New Items" });
  // a row's menu (portalled to the page): Escape closes it, the selection stays
  await more.click();
  await expect(menu).toBeVisible();
  await menu.getByRole("menuitem").first().focus();
  await page.keyboard.press("Escape");
  await expect(menu).toHaveCount(0);
  await expect.poll(() => checked(page)).toEqual(["Big Stacks"]);
  // Ctrl+A, Space and Enter... in the menu: not the list's
  await more.click();
  await expect(menu).toBeVisible();
  await menu.getByRole("menuitem").first().focus();
  await page.keyboard.press("Control+a");
  await expect.poll(() => checked(page)).toEqual(["Big Stacks"]);
  await page.keyboard.press("Escape");
  await expect(menu).toHaveCount(0);
  // the filter: Ctrl+A selects its text, Space types, Escape is the field's
  const filter = page.locator(".k-hero").getByLabel("Filter");
  await filter.focus();
  await page.keyboard.press("Control+a");
  await page.keyboard.press("Space");
  await page.keyboard.press("Escape");
  await expect.poll(() => checked(page)).toEqual(["Big Stacks"]);
  await filter.fill("");
  // a dialog: Escape closes it, the selection stays; Ctrl+A in its field is the field's
  await selbar(page).getByRole("button", { name: "Remove…" }).click();
  const dlg = page.getByRole("dialog", { name: "Remove 1 mod?" });
  await expect(dlg).toBeVisible();
  await page.keyboard.press("Control+a");
  await page.keyboard.press("Escape");
  await expect(dlg).toHaveCount(0);
  await expect.poll(() => checked(page)).toEqual(["Big Stacks"]);
  // on the list itself: Ctrl+A every row shown, Escape none
  await box(page, "Big Stacks").focus();
  await page.keyboard.press("Control+a");
  const n = await list(page).getByRole("checkbox").count();
  await expect.poll(async () => (await checked(page)).length).toBe(n);
  await page.keyboard.press("Escape");
  await expect.poll(() => checked(page)).toEqual([]);
});
