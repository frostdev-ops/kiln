// "In the game folder" (kiln_manage::found): mods a player extracted into the game folder, whether each loads with the
// profile, and the fix; the drop-in switches for every engine in the profile settings; Play's line. Browser mock only
// (src/api/mock.ts seeds the mock game folder; the "Ember run" profile has every drop-in folder off).
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

test("the Mods tab lists the game folder's mods, and one click loads EML's", async ({ page }) => {
  await desktop(page, "/profiles/ember-run/mods");
  const sec = page.getByRole("region", { name: "In the game folder" });
  const row = (name: string) => sec.locator('[role="row"], tr').filter({ hasText: name });
  await expect(row("Hoarder's Helper")).toContainText("Not loaded");
  await expect(row("Hoarder's Helper")).toContainText("EML drop-ins are off");
  await expect(row("Zoom Out")).toContainText("In this profile");
  await expect(row("Fly Mode")).toContainText("Loads");
  await expect(row("screenshots")).toContainText("Can't run");
  await row("Hoarder's Helper").getByRole("button", { name: "Load the game-folder EML mods" }).click();
  await expect(row("Hoarder's Helper")).toContainText("Loads");
  await expect(row("Less Dense Shroud Fog")).toContainText("Loads");
  const p = await api<{ dropins: string[]; dropinsEngines?: boolean }>(page, "profileGet", "ember-run");
  expect(p.dropins).toEqual(["eml"]);
  expect(p.dropinsEngines).toBe(true);
});

test("Add to this profile imports a Greeneye mod the profile's own Greeneye mods hide", async ({ page }) => {
  await desktop(page, "/profiles/ember-run/engines");
  const sec = page.getByRole("region", { name: "In the game folder" });
  const row = sec.locator('[role="row"], tr').filter({ hasText: "Better Map" });
  await expect(row).toContainText("only when every drop-in folder is on");
  await row.getByRole("button", { name: "Add to this profile" }).click();
  await expect(row).toContainText("In this profile");
  const p = await api<{ mods: Record<string, { id: string }[]> }>(page, "profileGet", "ember-run");
  expect(p.mods.greeneye.map((m) => m.id)).toContain("better-map");
});

test("profile settings switch every engine's folder, grouped", async ({ page }) => {
  await desktop(page, "/profiles/vanilla-plus/mods");
  await page.getByRole("button", { name: "Settings" }).first().click();
  const dlg = page.getByRole("dialog", { name: "Profile settings" });
  for (const name of ["Kiln mods", "EMF mods", "EML mods", "ShroudForge mods", "Greeneye mods", "Flame mods", "Shroudtopia mods"]) {
    await expect(dlg.getByRole("switch", { name: new RegExp(`^${name} `) })).toBeVisible();
  }
  await dlg.getByText("EML mods", { exact: true }).click();
  await expect(dlg.getByRole("switch", { name: /^EML mods / })).toBeChecked();
  await dlg.getByRole("button", { name: "Save" }).click();
  await expect.poll(async () => (await api<{ dropins: string[] }>(page, "profileGet", "vanilla-plus")).dropins).toEqual(["eml"]);
});

test("Play says how many game-folder mods the active profile leaves out and links to them", async ({ page }) => {
  await desktop(page, "/");
  const active = await api<{ profileId: string } | null>(page, "profileActive", "client-steam");
  test.skip(!active, "no active profile in this mock");
  const line = page.getByRole("button", { name: /mods? in the game folder do(es)? not load with this profile/ });
  await expect(line).toBeVisible();
  await line.click();
  await expect(page.getByRole("region", { name: "In the game folder" })).toBeVisible();
});
