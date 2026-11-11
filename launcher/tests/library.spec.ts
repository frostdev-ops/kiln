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

test("with typing turned off in Settings, Remove is one click; other dangerous dialogs still ask for the word", async ({ page }) => {
  // a player asked for this: a mod's id can be sixty characters long
  await library(page);
  const [r] = await unused(page);
  const name = await page.evaluate(async (id) => (await (await import("/src/api/commands.ts")).libraryList()).find((e) => e.id === id)!.name, r.id);
  await page.goto("/#/settings");
  const toggle = page.getByRole("switch", { name: /Type a mod's name to remove it/ });
  await expect(toggle).toBeChecked();
  await page.getByText("Type a mod's name to remove it", { exact: true }).click();
  await expect(toggle).not.toBeChecked();
  await page.goto("/#/library");
  await page.getByLabel("Filter").fill(name);
  await page.getByRole("button", { name: `More for ${name}` }).click();
  await page.getByRole("menuitem", { name: `Remove ${r.version}` }).click();
  const dialog = page.getByRole("dialog", { name: `Remove ${name} ${r.version}?` });
  await expect(dialog.getByRole("textbox")).toHaveCount(0);
  await dialog.getByRole("button", { name: "Remove" }).click();
  await expect(dialog).toBeHidden();
  expect(await inLibrary(page, r)).toBe(false);
  // Clean up (several mods at once) keeps its word
  await page.getByLabel("Filter").fill("");
  await page.getByRole("button", { name: /Clean up/ }).click();
  await expect(page.getByRole("dialog").getByRole("textbox")).toBeVisible();
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

const planCommand = (page: Page, cmd: string, plan: unknown) => page.evaluate(([c, p]) =>
  (window as unknown as { __kilnMock: { planCommand: (c: string, p: unknown) => void } }).__kilnMock.planCommand(c as string, p), [cmd, plan] as const);

// Remove goes past only the users the dialog named: Confirm waits for the backend's list, and a failed list forces past
// nobody (a user the dialog did not show stays a conflict).
test("Remove waits for what uses the version and never forces past users the dialog did not show", async ({ page }) => {
  await library(page);
  const [r] = await unused(page);
  const name = await page.evaluate(async (id) => (await (await import("/src/api/commands.ts")).libraryList()).find((e) => e.id === id)!.name, r.id);
  await gameFileUsers(page, { [key(r)]: ["ember-run (active in Enshrouded)"] });
  await page.getByLabel("Filter").fill(name);
  const open = async () => {
    await page.getByRole("button", { name: `More for ${name}` }).click();
    await page.getByRole("menuitem", { name: `Remove ${r.version}` }).click();
    return page.getByRole("dialog", { name: `Remove ${name} ${r.version}?` });
  };
  // still loading: Confirm stays disabled even with the word typed
  await planCommand(page, "library_users", { delay: 1500 });
  let dialog = await open();
  await dialog.getByRole("textbox").fill(r.id);
  await expect(dialog).toContainText("Checking what uses it.");
  await expect(dialog.getByRole("button", { name: "Remove" })).toBeDisabled();
  await expect(dialog).toContainText("Used by Ember run (active in Enshrouded).");
  await expect(dialog.getByRole("button", { name: "Remove" })).toBeEnabled();
  await dialog.getByRole("button", { name: "Cancel" }).click();
  await expect(dialog).toBeHidden();
  // the check failed: the dialog says so, and Remove stops at the user it could not name
  for (let i = 0; i < 2; i++) await planCommand(page, "library_users", { error: { code: "io", message: "Profiles are unreadable" } }); // and its one retry
  dialog = await open();
  await expect(dialog).toContainText("could not check what uses it", { timeout: 10_000 });
  await dialog.getByRole("textbox").fill(r.id);
  await dialog.getByRole("button", { name: "Remove" }).click();
  await expect(page.getByRole("alert").filter({ hasText: "is used by profile(s) ember-run (active in Enshrouded)" })).toBeVisible();
  expect(await inLibrary(page, r)).toBe(true);
});
