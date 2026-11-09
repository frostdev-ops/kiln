// The Library page's Remove and Clean up against what the backend knows: a game's own profile file (kiln/profile.json)
// can list a version no launcher profile lists any more, and Clean up goes in one call. Browser mock only.
import { expect, test, type Page } from "@playwright/test";

type Ref = { kind: string; id: string; version: string; hash: string };
type Mock = { calls: () => string[]; setGameFileUsers: (users: Record<string, string[]>) => void };
const calls = (page: Page, cmd: string) => page.evaluate((c) => (window as unknown as { __kilnMock: Mock }).__kilnMock.calls().filter((x) => x === c).length, cmd);
const key = (r: Ref) => `${r.kind}/${r.id}/${r.version}`;
const gameFileUsers = (page: Page, users: Record<string, string[]>) =>
  page.evaluate((u) => (window as unknown as { __kilnMock: Mock }).__kilnMock.setGameFileUsers(u), users);
const unused = (page: Page): Promise<Ref[]> => page.evaluate(async () => (await import("/src/api/commands.ts")).libraryUnused());
const inLibrary = (page: Page, r: Ref) => page.evaluate(async (x) =>
  (await (await import("/src/api/commands.ts")).libraryList()).some((e) => e.kind === x.kind && e.id === x.id && e.versions.some((v) => v.version === x.version)), r);

async function library(page: Page) {
  await page.goto("/#/library");
  await page.locator(".k-splash").waitFor({ state: "detached" });
}

test("Remove names what a game's profile file lists and removes past it", async ({ page }) => {
  await library(page);
  const [r] = await unused(page);
  const name = await page.evaluate(async (id) => (await (await import("/src/api/commands.ts")).libraryList()).find((e) => e.id === id)!.name, r.id);
  // no launcher profile lists r; the game's active profile file still does (the profile changed since it was activated)
  await gameFileUsers(page, { [key(r)]: ["ember-run (active in Enshrouded)"] });
  await page.getByLabel("Filter").fill(name);
  await page.getByRole("button", { name: `More for ${name}` }).click();
  await page.getByRole("menuitem", { name: `Remove ${r.version}` }).click();
  const dialog = page.getByRole("dialog", { name: `Remove ${name} ${r.version}?` });
  await expect(dialog).toContainText("Used by Ember run (active in Enshrouded). They will show it as missing.");
  await dialog.getByRole("textbox").fill(r.id);
  await dialog.getByRole("button", { name: "Remove" }).click();
  await expect(dialog).toBeHidden();
  await expect(page.locator(".k-toast", { hasText: "Removed" })).toBeVisible();
  expect(await inLibrary(page, r)).toBe(false);
});

test("Clean up removes the unused versions in one call and keeps what a game's profile file lists", async ({ page }) => {
  await library(page);
  const all = await unused(page);
  expect(all.length).toBeGreaterThan(2);
  const kept = all[0];
  await gameFileUsers(page, { [key(kept)]: ["ember-run (active in Enshrouded)"] });
  const before = { many: await calls(page, "library_remove_many"), one: await calls(page, "library_remove") };
  await page.getByRole("button", { name: "Clean up" }).click();
  const dialog = page.getByRole("dialog", { name: `Remove ${all.length - 1} unused?` });
  await dialog.getByRole("textbox").fill("clean");
  await dialog.getByRole("button", { name: "Remove" }).click();
  await expect(page.locator(".k-toast", { hasText: `Removed ${all.length - 1}` })).toBeVisible();
  expect(await calls(page, "library_remove_many")).toBe(before.many + 1);
  expect(await calls(page, "library_remove")).toBe(before.one);
  expect(await unused(page)).toEqual([]);
  expect(await inLibrary(page, kept)).toBe(true);
});

test("a running game stops Clean up before anything is removed", async ({ page }) => {
  await library(page);
  const all = await unused(page);
  await page.evaluate(() => (window as unknown as { __kilnMock: { planCommand: (c: string, p: unknown) => void } }).__kilnMock
    .planCommand("library_remove_many", { error: { code: "game_running", message: "Close Enshrouded first: the game may be reading this mod." } }));
  await page.getByRole("button", { name: "Clean up" }).click();
  const dialog = page.getByRole("dialog", { name: `Remove ${all.length} unused?` });
  await dialog.getByRole("textbox").fill("clean");
  await dialog.getByRole("button", { name: "Remove" }).click();
  await expect(page.getByRole("alert").filter({ hasText: "Close Enshrouded first" })).toBeVisible();
  expect(await unused(page)).toEqual(all);
});
