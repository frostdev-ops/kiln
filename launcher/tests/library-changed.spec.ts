// Nexus report of 2026-11-13 (browser mock: the seeded launcher has "Ember run" loaded in the Steam game): a mod changed
// in its own Library folder ("Open folder", mod.json edited) is marked "Changed on disk" with "Use the changed files",
// and a Play it stops says which mod and offers the same, then starts.
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
type Mods = Record<string, { id: string; version: string; hash: string; enabled: boolean }[]>;
const profileMods = async (page: Page, id = "ember-run") => (await api<{ mods: Mods }>(page, "profileGet", id)).mods;
const list = (page: Page) => page.locator(".k-hero").getByRole("list", { name: "Mods" });
const row = (page: Page, name: string) => list(page).getByRole("listitem").filter({ has: page.getByText(name, { exact: true }) });
const edit = (page: Page, kind: string, id: string, change: { name?: string; version?: string }) =>
  page.evaluate(([k, i, c]) => (window as unknown as { __kilnMock: { editInLibrary: (k: string, i: string, c: unknown) => void } }).__kilnMock.editInLibrary(k as string, i as string, c),
    [kind, id, change] as const);
/** The list reads the Library again when the main screen mounts again. */
async function back(page: Page) {
  await page.evaluate(() => { location.hash = "#/settings"; });
  await expect(page.getByRole("region", { name: "Profiles" })).toBeVisible();
  await page.evaluate(() => { location.hash = "#/"; });
}

test("a mod changed in its Library folder shows it, and Use the changed files shows its new name", async ({ page }) => {
  await desktop(page);
  await expect(row(page, "Loot Tables X")).toBeVisible({ timeout: 10_000 });
  await edit(page, "eml", "loot-tables-x", { name: "Loot Tables X (English)" });
  await back(page);
  const r = row(page, "Loot Tables X");
  await expect(r).toContainText("Changed on disk", { timeout: 10_000 });
  await r.getByRole("button", { name: "Use the changed files of Loot Tables X" }).click();
  await expect(page.locator(".k-toast", { hasText: "Loot Tables X (English) uses the changed files" })).toBeVisible();
  const after = row(page, "Loot Tables X (English)");
  await expect(after).toBeVisible();
  await expect(after).not.toContainText("Changed on disk");
  await expect(row(page, "Loot Tables X")).toHaveCount(0);
});

test("Play stopped by a mod changed in its folder names it; Use the changed files takes it over and starts", async ({ page }) => {
  await desktop(page);
  await expect(row(page, "Better Chests").getByRole("switch", { name: "Better Chests" })).toHaveAttribute("aria-checked", "true", { timeout: 10_000 });
  const before = (await profileMods(page)).shroudforge[0];
  await edit(page, "shroudforge", "better-chests", { name: "Better Chests EN", version: "0.4.2" });
  const play = page.locator(".k-door");
  await expect(play).toBeEnabled({ timeout: 10_000 });
  await play.click();
  const said = page.locator(".k-toast", { hasText: "Better Chests was changed in its folder" });
  await expect(said).toBeVisible({ timeout: 10_000 });
  await expect(said).not.toContainText("library hash mismatch");
  await said.getByRole("button", { name: "Use the changed files" }).click();
  // taken over, then Play again: the game starts, the profile lists the new entry
  await expect(page.locator(".k-head-state")).toHaveText(/Starting|Running/, { timeout: 15_000 });
  const now = (await profileMods(page)).shroudforge[0];
  expect(now).toMatchObject({ id: "better-chests", version: "0.4.2", enabled: true });
  expect(now.hash).not.toBe(before.hash);
});
