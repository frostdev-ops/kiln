// Nexus report of 2026-11-13 (browser mock: the seeded launcher has "Ember run" loaded in the Steam game): a profile
// deleted and made again under the same name takes edits, never the deleted one's generation or mods.
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

test("a profile deleted and made again under the same name takes the next switch (not 'changed meanwhile')", async ({ page }) => {
  await desktop(page);
  // Ember run is edited a few times: the launcher holds it at a higher generation than a new profile starts with
  const sw = (name: string) => row(page, name).getByRole("switch", { name });
  await expect(sw("Better Chests")).toHaveAttribute("aria-checked", "true", { timeout: 10_000 });
  for (const on of ["false", "true", "false", "true"]) {
    await sw("Better Chests").click();
    await expect(sw("Better Chests")).toHaveAttribute("aria-checked", on);
    await expect.poll(async () => (await profileMods(page)).shroudforge[0].enabled).toBe(on === "true");
  }
  // Settings: another profile loaded, Ember run deleted, a new one saved under its name and loaded
  await page.evaluate(() => { location.hash = "#/settings"; });
  const sec = page.getByRole("region", { name: "Profiles" });
  await sec.getByRole("listitem").filter({ hasText: "Vanilla plus" }).getByRole("button", { name: "Load" }).click();
  await expect(sec.getByRole("listitem").filter({ hasText: "Vanilla plus" })).toContainText("Loaded", { timeout: 5000 });
  await sec.getByRole("button", { name: "More for Ember run" }).click();
  await page.getByRole("menuitem", { name: "Delete" }).click();
  const confirm = page.getByRole("dialog", { name: "Delete Ember run?" });
  await confirm.getByLabel(/Type/).fill("ember-run");
  await confirm.getByRole("button", { name: "Delete", exact: true }).click();
  await expect(sec.getByRole("listitem").filter({ hasText: "Ember run" })).toHaveCount(0);
  await sec.getByRole("button", { name: "Save current as…" }).click();
  const dialog = page.getByRole("dialog", { name: "Save current as" });
  await dialog.getByLabel("Name").fill("Ember run");
  await dialog.getByRole("button", { name: "Save", exact: true }).click();
  const again = sec.getByRole("listitem").filter({ hasText: "Ember run" });
  await expect(again).toBeVisible();
  const made = await api<{ id: string; generation: number; mods: Mods }>(page, "profileGet", "ember-run");
  expect(made.generation).toBe(1);
  await again.getByRole("button", { name: "Load" }).click();
  await expect(again).toContainText("Loaded", { timeout: 5000 });
  // back on Play: a switch saves the new profile, with its own mods (Vanilla plus's), and no conflict
  await page.evaluate(() => { location.hash = "#/"; });
  await expect(sw("Weather Plus")).toHaveAttribute("aria-checked", "true", { timeout: 10_000 });
  await sw("Weather Plus").click();
  await expect(sw("Weather Plus")).toHaveAttribute("aria-checked", "false");
  await expect.poll(async () => (await profileMods(page)).kiln.find((m) => m.id === "weather-plus")?.enabled).toBe(false);
  await expect(page.locator(".k-toast", { hasText: /changed elsewhere|changed meanwhile/ })).toHaveCount(0);
  const saved = await profileMods(page);
  expect(saved).toEqual({ ...made.mods, kiln: made.mods.kiln.map((m) => (m.id === "weather-plus" ? { ...m, enabled: false } : m)) });
});
