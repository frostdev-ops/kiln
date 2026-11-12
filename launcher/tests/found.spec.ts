// "In the game folder" (kiln_manage::found): mods a player extracted into the game folder, whether each loads with the
// loaded profile, and the fix, as rows of Play's list of every mod; the drop-in switches for every engine in a profile's
// folders and notes. Browser mock only (src/api/mock.ts seeds the mock game folder; the "Ember run" profile has every
// drop-in folder off).
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
/** A game-folder row of Play's list (it says "Game folder"; a Library row of the same name does not). */
const folderRow = (page: Page, name: string) => page.locator(".k-hero").getByRole("list", { name: "Mods" }).locator("li.found")
  .filter({ has: page.getByText(name, { exact: true }) });

test("Play's list shows the game folder's mods, and one click loads EML's", async ({ page }) => {
  await desktop(page, "/");
  const row = (name: string) => folderRow(page, name);
  await expect(row("Hoarder's Helper")).toContainText("Game folder");
  await expect(row("Hoarder's Helper")).toContainText("Does not load");
  await expect(row("Hoarder's Helper").locator(".k-mod-note span[title]")).toHaveAttribute("title", /EML drop-ins are off/);
  await expect(row("Zoom Out")).toContainText("A copy is in your list");
  await expect(row("Fly Mode")).toContainText("Loads from the game folder");
  await expect(row("screenshots")).toContainText("Can't run");
  // no switch the backend could not honour
  await expect(row("Hoarder's Helper").getByRole("switch")).toHaveCount(0);
  await row("Hoarder's Helper").getByRole("button", { name: "Load the game-folder EML mods" }).click();
  await expect(row("Hoarder's Helper")).toContainText("Loads from the game folder");
  await expect(row("Less Dense Shroud Fog")).toContainText("Loads from the game folder");
  const p = await api<{ dropins: string[]; dropinsEngines?: boolean }>(page, "profileGet", "ember-run");
  expect(p.dropins).toEqual(["eml"]);
  expect(p.dropinsEngines).toBe(true);
});

test("a Greeneye mod follows Greeneye's switch, whatever the profile lists", async ({ page }) => {
  await desktop(page, "/");
  const row = folderRow(page, "Better Map");
  await expect(row.locator(".k-mod-note span[title]")).toHaveAttribute("title", /Greeneye drop-ins are off/);
  await row.getByRole("button", { name: "Load the game-folder Greeneye mods" }).click();
  await expect(row).toContainText("Loads from the game folder");
  const p = await api<{ dropins: string[] }>(page, "profileGet", "ember-run");
  expect(p.dropins).toEqual(["greeneye"]);
});

test("Add imports a mod from a folder its loader does not read, and it is on", async ({ page }) => {
  await desktop(page, "/");
  const row = folderRow(page, "Pack Mule");
  await expect(row.locator(".k-mod-note span[title]")).toHaveAttribute("title", /Kiln looks for them in emf\/mods/);
  await row.getByRole("button", { name: "Add Pack Mule to my mods" }).click();
  await expect(row).toContainText("A copy is in your list");
  const p = await api<{ mods: Record<string, { id: string; enabled: boolean }[]> }>(page, "profileGet", "ember-run");
  expect(p.mods.emf.find((m) => m.id === "pack-mule")?.enabled).toBe(true);
  // the Library copy is a row of its own, on
  await expect(page.locator(".k-hero").getByRole("switch", { name: "Pack Mule" })).toHaveAttribute("aria-checked", "true");
});

test("a profile's folders and notes switch every engine's folder, grouped", async ({ page }) => {
  await desktop(page, "/settings");
  await page.getByRole("region", { name: "Profiles" }).getByRole("listitem").filter({ hasText: "Vanilla plus" }).getByRole("button", { name: "More for Vanilla plus" }).click();
  await page.getByRole("menuitem", { name: "Folders and notes" }).click();
  const dlg = page.getByRole("dialog", { name: "Vanilla plus: folders and notes" });
  for (const name of ["Kiln mods", "EMF mods", "EML mods", "ShroudForge mods", "Greeneye mods", "Flame mods", "Shroudtopia mods", "Enshwick mods"]) {
    await expect(dlg.getByRole("switch", { name: new RegExp(`^${name} `) })).toBeVisible();
  }
  // "Vanilla plus" was made before dev.22, when these plugins read their folder without asking: shown as on
  for (const name of ["Greeneye mods", "Flame mods", "Shroudtopia mods", "Enshwick mods"]) {
    await expect(dlg.getByRole("switch", { name: new RegExp(`^${name} `) })).toBeChecked();
  }
  await expect(dlg.getByRole("switch", { name: /^EML mods / })).not.toBeChecked();
  await dlg.getByText("EML mods", { exact: true }).click();
  await expect(dlg.getByRole("switch", { name: /^EML mods / })).toBeChecked();
  await dlg.getByRole("button", { name: "Save" }).click();
  // saved as the explicit list: what it loaded before, plus EML
  await expect.poll(async () => (await api<{ dropins: string[] }>(page, "profileGet", "vanilla-plus")).dropins)
    .toEqual(["eml", "greeneye", "flame", "shroudtopia", "enshwick", "shroudlink", "ember"]);
  expect((await api<{ dropinsEngines?: boolean }>(page, "profileGet", "vanilla-plus")).dropinsEngines).toBe(true);
});

test("Play counts the game folder's mods that do not load as off, and lists them in place", async ({ page }) => {
  await desktop(page, "/");
  const active = await api<{ profileId: string } | null>(page, "profileActive", "client-steam");
  test.skip(!active, "no active profile in this mock");
  // the old line "N mods in the game folder do not load with this profile" is gone: they are rows, counted as off
  await expect(page.getByRole("button", { name: /mods? in the game folder do(es)? not load with this profile/ })).toHaveCount(0);
  await expect(page.locator(".k-hero .k-hero-say")).toHaveText(/^12 mods on, 15 off\./);
  await expect(page.locator(".k-hero li.found").filter({ hasText: "Does not load" })).toHaveCount(4);
});
