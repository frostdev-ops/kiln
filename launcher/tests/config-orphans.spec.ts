// The Configs window and the configs of mods that are not installed (0.2.0-dev.31.31; HTR, #general: Mods, More, Configs
// listed the configs of mods he had removed, and their switches looked live). Remove keeps settings on purpose, so the
// files stay; the window now groups them under "Not installed: kept for when you add it back", read-only, with Clear on
// each and Clear all (a confirmation, then the files go into the settings backups and a toast says where). An installed
// mod's config, and the config of a mod that is only switched off, are never touched or marked. Browser mock only
// (src/api/mock.ts: config_list marks the orphans as kiln_manage::config_orphans does; `__kilnMock.configsCleared()`).
import { expect, test, type Page } from "@playwright/test";

const SHOTS = process.env.KILN_SHOTS_DIR;

async function desktop(page: Page, route = "/") {
  await page.goto(`/#${route}`);
  await page.locator(".k-splash").waitFor({ state: "detached" });
}
const list = (page: Page) => page.locator(".k-hero").getByRole("list", { name: "Mods" });
const row = (page: Page, name: string) => list(page).getByRole("listitem").filter({ has: page.getByText(name, { exact: true }) });
const cleared = (page: Page) => page.evaluate(() => (window as unknown as { __kilnMock: { configsCleared: () => string[] } }).__kilnMock.configsCleared());
const call = <T,>(page: Page, fn: string, ...args: unknown[]) =>
  page.evaluate(async ([f, a]) => {
    const m = (await import("/src/api/commands.ts")) as unknown as Record<string, (...x: unknown[]) => Promise<unknown>>;
    try { return { ok: await m[f as string](...(a as unknown[])) }; } catch (e) { return { error: e as { code: string; message: string } }; }
  }, [fn, args] as const) as Promise<{ ok?: T; error?: { code: string; message: string } }>;

/** Remove (the settings box left unchecked: the settings stay, as Remove does by default). */
async function remove(page: Page, name: string) {
  await row(page, name).getByRole("button", { name: `More for ${name}` }).click({ timeout: 10_000 });
  await page.getByRole("menuitem", { name: "Remove", exact: true }).click();
  const dialog = page.getByRole("dialog", { name: `Remove ${name}?` });
  await dialog.getByRole("button", { name: "Remove", exact: true }).click();
  await expect(dialog).toBeHidden();
  await expect(row(page, name)).toHaveCount(0);
}

const files = (page: Page) => page.getByRole("listbox", { name: "Config files" });
const option = (page: Page, name: string) => files(page).getByRole("option").filter({ has: page.getByText(name, { exact: true }) });
const GROUP = "Not installed: kept for when you add it back";

test("a removed mod's config is listed as not installed, read-only, in Mods, More, Configs", async ({ page }, testInfo) => {
  await desktop(page);
  await remove(page, "New Items");
  // HTR's way in: the menu of a mod that is still there
  await row(page, "Beastmaster").getByRole("button", { name: "More for Beastmaster" }).click();
  await page.getByRole("menuitem", { name: "Configs", exact: true }).click();
  const dialog = page.getByRole("dialog", { name: "Configs" });
  await expect(dialog.getByText(GROUP)).toBeVisible();
  // the installed mods' files come first and are as they were; the removed mod's file sits under the heading
  await expect(option(page, "beastmaster")).toBeVisible();
  await expect(dialog.getByRole("button", { name: "Clear the settings of beastmaster" })).toHaveCount(0);
  const head = await dialog.getByText(GROUP).boundingBox();
  const orphan = await option(page, "new-items").boundingBox();
  const installed = await option(page, "big-stacks").boundingBox();
  expect(installed!.y).toBeLessThan(head!.y);
  expect(orphan!.y).toBeGreaterThan(head!.y);
  // the open file is Beastmaster's, a live form
  await expect(dialog.getByRole("switch").first()).toBeEnabled();
  await expect(dialog.getByText("is not installed.")).toHaveCount(0);
  // the removed mod's file: a hint, and switches and fields that cannot be changed
  await option(page, "new-items").click();
  await expect(dialog.getByText("new-items is not installed. Its settings are kept for when you add it back.")).toBeVisible();
  await expect(dialog.getByText("changes do nothing until the mod is back")).toBeVisible();
  const switches = dialog.getByRole("switch");
  expect(await switches.count()).toBeGreaterThan(0);
  for (let i = 0; i < (await switches.count()); i++) await expect(switches.nth(i)).toBeDisabled();
  for (const box of await dialog.getByRole("spinbutton").all()) await expect(box).toBeDisabled();
  // the raw file is read-only too
  await dialog.getByRole("radiogroup", { name: "View" }).getByRole("radio", { name: "File" }).click();
  await expect(dialog.getByRole("textbox", { name: "File" })).toHaveAttribute("readonly", "");
  await expect(dialog.getByRole("button", { name: "Save" })).toHaveCount(0);
  if (SHOTS) await page.screenshot({ path: `${SHOTS}/configs-orphans-modal.png` });
  await testInfo.attach("configs-orphans-modal", { body: await page.screenshot(), contentType: "image/png" });
});

test("the Configs page: two removed mods, Clear all asks first, then the files go and the toast says where the copy is", async ({ page }, testInfo) => {
  await desktop(page);
  await remove(page, "New Items");
  await remove(page, "Beastmaster");
  await desktop(page, "/mods/configs");
  await expect(page.getByText(GROUP)).toBeVisible();
  await expect(option(page, "new-items")).toBeVisible();
  await expect(option(page, "beastmaster")).toBeVisible();
  await expect(option(page, "big-stacks")).toBeVisible();
  await option(page, "beastmaster").click();
  await expect(page.getByText("beastmaster is not installed.")).toBeVisible();
  await expect(page.getByRole("switch").first()).toBeDisabled(); // the form is drawn (and read-only) before the picture
  if (SHOTS) await page.screenshot({ path: `${SHOTS}/configs-orphans-page.png` });
  await testInfo.attach("configs-orphans-page", { body: await page.screenshot(), contentType: "image/png" });

  // Clear all: a confirmation first, naming the files; Cancel changes nothing
  await page.getByRole("button", { name: "Clear all" }).click();
  const banner = page.getByRole("status").filter({ hasText: "Clear the settings of every mod that is not installed?" });
  await expect(banner).toBeVisible();
  await expect(banner).toContainText("emf/beastmaster.ini");
  await expect(banner).toContainText("new-items.json");
  await expect(banner).toContainText("The settings of mods you have installed are never touched.");
  await banner.getByRole("button", { name: "Cancel" }).click();
  await expect(banner).toBeHidden();
  expect(await cleared(page)).toEqual([]);

  await page.getByRole("button", { name: "Clear all" }).click();
  await banner.getByRole("button", { name: "Clear", exact: true }).click();
  await expect(page.getByText("Settings cleared")).toBeVisible();
  await expect(page.getByText(/A copy is in .*backups\/mod-settings\/config-ember-run-/)).toBeVisible();
  expect((await cleared(page)).sort()).toEqual(["emf/beastmaster.ini", "new-items.json"]);
  // the group is gone, the installed mod's file is still there and live
  await expect(page.getByText(GROUP)).toHaveCount(0);
  await expect(option(page, "new-items")).toHaveCount(0);
  await expect(option(page, "big-stacks")).toBeVisible();
  await expect(page.getByRole("button", { name: "Clear all" })).toHaveCount(0);
});

test("Clear on one entry takes that file only", async ({ page }) => {
  await desktop(page);
  await remove(page, "New Items");
  await remove(page, "Beastmaster");
  await desktop(page, "/mods/configs");
  await page.getByRole("button", { name: "Clear the settings of new-items" }).click();
  const banner = page.getByRole("status").filter({ hasText: "Clear the settings of new-items?" });
  await expect(banner).toContainText("new-items.json");
  await expect(banner).not.toContainText("emf/beastmaster.ini");
  await banner.getByRole("button", { name: "Clear", exact: true }).click();
  await expect(page.getByText("Settings cleared")).toBeVisible();
  expect(await cleared(page)).toEqual(["new-items.json"]);
  await expect(option(page, "new-items")).toHaveCount(0);
  await expect(option(page, "beastmaster")).toBeVisible();
  // the card's own Clear works the same way
  await option(page, "beastmaster").click();
  await page.getByRole("button", { name: "Clear settings", exact: true }).click();
  await page.getByRole("status").filter({ hasText: "Clear the settings of beastmaster?" }).getByRole("button", { name: "Clear", exact: true }).click();
  await expect.poll(() => cleared(page)).toEqual(["new-items.json", "emf/beastmaster.ini"]);
});

test("on the Deck's screen, Clear on a file far down a long list scrolls its confirmation into view", async ({ page }) => {
  // review of 0.2.0-dev.31.31: the confirmation strip sits above the list; a bin far down the list (the window scrolled
  // to it) asked out of sight, so Clear looked as if it did nothing
  await page.setViewportSize({ width: 1280, height: 800 });
  await desktop(page);
  await page.evaluate(() => {
    const m = (window as unknown as { __kilnMock: { addConfig: (t: { profile: string }, f: string, mod: string) => void } }).__kilnMock;
    for (let i = 1; i <= 14; i++) m.addConfig({ profile: "ember-run" }, `gone-${String(i).padStart(2, "0")}.json`, `gone-${String(i).padStart(2, "0")}`);
  });
  await row(page, "Beastmaster").getByRole("button", { name: "More for Beastmaster" }).click();
  await page.getByRole("menuitem", { name: "Configs", exact: true }).click();
  const dialog = page.getByRole("dialog", { name: "Configs" });
  await expect(dialog.getByText(GROUP)).toBeVisible();
  await dialog.getByRole("button", { name: "Clear the settings of gone-14" }).click();
  const banner = dialog.getByRole("status").filter({ hasText: "Clear the settings of gone-14?" });
  await expect(banner).toBeInViewport();
  await expect(banner.getByRole("button", { name: "Clear", exact: true })).toBeInViewport();
  await banner.getByRole("button", { name: "Cancel" }).click();
  expect(await cleared(page)).toEqual([]);
});

test("a mod that is only switched off is installed: its config is not marked, and the backend refuses to clear it", async ({ page }) => {
  await desktop(page);
  await row(page, "Beastmaster").getByRole("switch", { name: "Beastmaster" }).click();
  await expect(row(page, "Beastmaster").getByRole("switch", { name: "Beastmaster" })).toHaveAttribute("aria-checked", "false");
  await desktop(page, "/mods/configs");
  await expect(option(page, "beastmaster")).toBeVisible();
  await expect(page.getByText(GROUP)).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Clear all" })).toHaveCount(0);
  // the backend answers the same to a request that names an installed mod's file (a stale window, a hand-made call)
  for (const file of ["emf/beastmaster.ini", "big-stacks.json"]) {
    const r = await call(page, "configClearOrphans", { profile: "ember-run" }, [file]);
    expect(r.error?.code, file).toBe("conflict");
  }
  expect(await cleared(page)).toEqual([]);
  await expect(option(page, "big-stacks")).toBeVisible();
});

test("a server's configs are never marked, and the profile's own files are as they were with nothing removed", async ({ page }) => {
  await desktop(page, "/mods/configs");
  await expect(option(page, "new-items")).toBeVisible();
  await expect(page.getByText(GROUP)).toHaveCount(0);
  // a stopped server edits its files with the same window: nothing there is marked (its mods are not this profile's)
  await desktop(page, "/servers/lab/overview");
  await page.evaluate(() => (window as unknown as { __kilnMock: { setServer: (s: { running: boolean }) => void } }).__kilnMock.setServer({ running: false }));
  await desktop(page, "/servers/lab/configs");
  await expect(option(page, "new-items")).toBeVisible();
  await expect(page.getByText(GROUP)).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Clear all" })).toHaveCount(0);
});
