// Removing a mod (a row's menu on Play) and Clean up (Settings, Library) against what the backend knows: a game's own
// profile file (kiln/profile.json, play-once.ini) can list a version no launcher profile lists any more, and Clean up goes
// in one call. Remove is one click with a plain confirmation unless "Type a mod's name to remove it" is on. Browser mock
// only.
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
const nameOf = (page: Page, r: Ref) => page.evaluate(async (id) => (await (await import("/src/api/commands.ts")).libraryList()).find((e) => e.id === id)!.name, r.id);

async function open(page: Page, route = "/") {
  await page.goto(`/#${route}`);
  await page.locator(".k-splash").waitFor({ state: "detached" });
}
/** Remove on a row of Play's list: its menu, then the confirmation. The row is brought into view first and the list's
 *  scroll event let through: a menu closes when its own list scrolls (ui/Menu.tsx), and a scroll followed at once by the
 *  click can deliver that event after the menu opened. */
async function removeFromList(page: Page, name: string) {
  const row = page.locator(".k-hero").getByRole("list", { name: "Mods" }).locator("li:not(.found)").filter({ has: page.getByText(name, { exact: true }) });
  const more = row.getByRole("button", { name: `More for ${name}` });
  await more.scrollIntoViewIfNeeded();
  await page.evaluate(() => new Promise((done) => requestAnimationFrame(() => requestAnimationFrame(() => done(null)))));
  await more.click();
  await page.getByRole("menuitem", { name: "Remove", exact: true }).click();
  return page.getByRole("dialog", { name: `Remove ${name}?` });
}
async function typingOn(page: Page) {
  await open(page, "/settings");
  const toggle = page.getByRole("switch", { name: /Type a mod's name to remove it/ });
  await expect(toggle).not.toBeChecked();
  await page.getByText("Type a mod's name to remove it", { exact: true }).click();
  await expect(toggle).toBeChecked();
}
async function cleanUp(page: Page) {
  await open(page, "/settings");
  await page.getByRole("region", { name: "Library" }).getByRole("button", { name: "Clean up" }).click();
}

test("Remove names what a game's profile file lists and removes past it", async ({ page }) => {
  await open(page);
  const [r] = await unused(page);
  const name = await nameOf(page, r);
  // no launcher profile lists r; a game's play-once file still names Vanilla plus with it
  await gameFileUsers(page, { [key(r)]: ["vanilla-plus (play-once in Enshrouded)"] });
  const dialog = await removeFromList(page, name);
  await expect(dialog).toContainText("Its files leave the Library. Vanilla plus (play-once in Enshrouded) lists it too and will show it as missing.");
  await dialog.getByRole("button", { name: "Remove" }).click();
  await expect(dialog).toBeHidden();
  await expect(page.locator(".k-toast", { hasText: `Removed ${name}` })).toBeVisible();
  expect(await inLibrary(page, r)).toBe(false);
});

test("Remove is one click by default; with typing turned on in Settings it asks for the word; Clean up always does", async ({ page }) => {
  // a player asked for this: a mod's id can be sixty characters long
  await open(page);
  const [r, r2] = await unused(page);
  const name = await nameOf(page, r);
  let dialog = await removeFromList(page, name);
  await expect(dialog.getByRole("textbox")).toHaveCount(0);
  await dialog.getByRole("button", { name: "Remove" }).click();
  await expect(dialog).toBeHidden();
  expect(await inLibrary(page, r)).toBe(false);
  // turned on: the typed word comes back
  await typingOn(page);
  await open(page);
  const name2 = await nameOf(page, r2);
  dialog = await removeFromList(page, name2);
  await expect(dialog.getByRole("textbox")).toBeVisible();
  await expect(dialog.getByRole("button", { name: "Remove" })).toBeDisabled();
  await dialog.getByRole("textbox").fill(r2.id);
  await dialog.getByRole("button", { name: "Remove" }).click();
  await expect(dialog).toBeHidden();
  expect(await inLibrary(page, r2)).toBe(false);
  // Clean up (several mods at once) keeps its word whatever the setting says
  await page.evaluate(() => localStorage.setItem("kiln.confirm.typeNames", "0"));
  await cleanUp(page);
  await expect(page.getByRole("dialog").getByRole("textbox")).toBeVisible();
});

test("Clean up removes the unused versions in one call and keeps what a game's profile file lists", async ({ page }) => {
  await open(page);
  const all = await unused(page);
  expect(all.length).toBeGreaterThan(2);
  const kept = all[0];
  await gameFileUsers(page, { [key(kept)]: ["ember-run (active in Enshrouded)"] });
  const before = { many: await calls(page, "library_remove_many"), one: await calls(page, "library_remove") };
  await cleanUp(page);
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
  await open(page);
  const all = await unused(page);
  await page.evaluate(() => (window as unknown as { __kilnMock: { planCommand: (c: string, p: unknown) => void } }).__kilnMock
    .planCommand("library_remove_many", { error: { code: "game_running", message: "Close Enshrouded first: the game may be reading this mod." } }));
  await cleanUp(page);
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
  await page.addInitScript(() => {
    const rejected: string[] = [];
    Object.assign(window, { __rejectedActions: rejected });
    window.addEventListener("unhandledrejection", (event) => rejected.push(String(event.reason?.message ?? event.reason)));
  });
  // the typed word on, so the retry below can go by keyboard
  await typingOn(page);
  await open(page);
  const [r] = await unused(page);
  const name = await nameOf(page, r);
  await gameFileUsers(page, { [key(r)]: ["vanilla-plus (play-once in Enshrouded)"] });
  // still loading: Confirm stays disabled even with the word typed
  await planCommand(page, "library_users", { delay: 1500 });
  let dialog = await removeFromList(page, name);
  await dialog.getByRole("textbox").fill(r.id);
  await expect(dialog).toContainText("Checking what uses it.");
  await expect(dialog.getByRole("button", { name: "Remove" })).toBeDisabled();
  await expect(dialog).toContainText("Vanilla plus (play-once in Enshrouded) lists it too");
  await expect(dialog.getByRole("button", { name: "Remove" })).toBeEnabled();
  await dialog.getByRole("button", { name: "Cancel" }).click();
  await expect(dialog).toBeHidden();
  // the check failed: the dialog says so, and Remove stops at the user it could not name
  for (let i = 0; i < 2; i++) await planCommand(page, "library_users", { error: { code: "io", message: "Profiles are unreadable" } }); // and its one retry
  dialog = await removeFromList(page, name);
  await expect(dialog).toContainText("could not check what else uses it", { timeout: 10_000 });
  await dialog.getByRole("textbox").fill(r.id);
  await dialog.getByRole("button", { name: "Remove" }).click();
  await expect(page.getByRole("alert").filter({ hasText: "is used by profile(s) vanilla-plus (play-once in Enshrouded)" })).toBeVisible();
  expect(await inLibrary(page, r)).toBe(true);
  await expect(dialog).toContainText("Vanilla plus (play-once in Enshrouded) lists it too");
  await expect(dialog.getByRole("button", { name: "Remove" })).toBeEnabled();
  expect(await page.evaluate(() => (window as unknown as { __rejectedActions: string[] }).__rejectedActions)).toEqual([]);
  // Retry by keyboard with the freshly named users: success still closes the dialog and removes the version.
  await dialog.getByRole("textbox").press("Enter");
  await expect(dialog).toBeHidden();
  expect(await inLibrary(page, r)).toBe(false);
});
