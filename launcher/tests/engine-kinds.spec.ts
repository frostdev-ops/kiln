// Other engines' mods in the launcher (docs/kiln/engines.md): Library kinds and labels, a profile's Engines tab
// (switch, add, ShroudForge settings), the engine status on Logs, EML's "Restore vanilla game data", the Deck's Mods.
// Browser mock only (src/api/mock.ts seeds one mod per imported kind; "Ember run" holds four of them).
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

test("the library lists every engine kind with its label, and filters by engine", async ({ page }) => {
  await desktop(page, "/library");
  const table = page.getByRole("table", { name: "Library" }).or(page.locator('[aria-label="Library"]')).first();
  for (const [name, badge] of [["Loot Tables X", "EML"], ["Better Chests", "ShroudForge"], ["FOV Tweak", "SET"], ["Zoom Out", "Greeneye"],
    ["Creative Build", "Flame"], ["Fly Mode", "Shroudtopia"]] as const) {
    const row = table.locator('[role="row"], tr').filter({ hasText: name });
    await expect(row).toContainText(badge);
  }
  await page.getByRole("radio", { name: "Other engines" }).or(page.getByRole("button", { name: "Other engines" })).or(page.getByRole("tab", { name: "Other engines" })).first().click();
  await expect(table).toContainText("Better Chests");
  await expect(table).not.toContainText("New Items");
  await page.getByLabel("Engine").selectOption("shroudforge");
  await expect(table).toContainText("Better Chests");
  await expect(table).not.toContainText("Fly Mode");
});

test("a profile's Engines tab switches an engine mod and adds one from the library", async ({ page }) => {
  await desktop(page, "/profiles/ember-run/engines");
  const sf = page.locator('[data-kind="shroudforge"]');
  await expect(sf).toContainText("Better Chests");
  await sf.getByRole("button", { name: "Light Better Chests" }).click();
  await expect.poll(async () => (await api<{ mods: Record<string, { id: string; enabled: boolean }[]> }>(page, "profileGet", "ember-run"))
    .mods.shroudforge.find((m) => m.id === "better-chests")?.enabled).toBe(false);
  await expect(sf.getByRole("button", { name: "Light Better Chests" })).toHaveAttribute("aria-pressed", "false");

  // Flame: in the library, not in the profile yet
  const flame = page.locator('[data-kind="flame"]');
  await flame.getByRole("button", { name: "Add from the library" }).click();
  const dialog = page.getByRole("dialog", { name: "Add from the library" });
  await dialog.getByText("Creative Build").click();
  await dialog.getByRole("button", { name: /^Add 1/ }).click();
  await expect(flame).toContainText("Creative Build");
  await expect.poll(async () => (await api<{ mods: Record<string, { id: string }[]> }>(page, "profileGet", "ember-run")).mods.flame.map((m) => m.id)).toEqual(["creative-build"]);
  await expect(page.getByRole("link", { name: /Engines/ })).toContainText("5");
});

test("ShroudForge settings edit a value through the SchemaForm; kinds without settings say so", async ({ page }) => {
  await desktop(page, "/profiles/ember-run/engines");
  await page.locator('[data-kind="shroudforge"]').getByRole("button", { name: "More for Better Chests" }).click();
  await page.getByRole("menuitem", { name: "Settings" }).click();
  const dialog = page.getByRole("dialog", { name: "Better Chests settings" });
  await expect(dialog).toContainText("extended.mod.json");
  await expect(dialog).toContainText("Storage");
  const sort = dialog.getByRole("switch", { name: "autoSort" });
  await expect(sort).not.toBeChecked();
  await dialog.locator('label.k-toggle[for="sf-autoSort"]').click();
  await expect(sort).toBeChecked();
  await expect.poll(async () => (await api<{ values: Record<string, unknown> }>(page, "engineSettingsGet", "shroudforge", "better-chests", "ember-run", "client-steam")).values.autoSort).toBe(true);
  await dialog.getByRole("button", { name: "Done" }).click();
  await expect(dialog).toHaveCount(0);

  // Shroudtopia: values from shroudtopia.json
  const more = page.locator('[data-kind="shroudtopia"]').getByRole("button", { name: "More for Fly Mode" });
  await more.scrollIntoViewIfNeeded();
  await more.click();
  await page.getByRole("menuitem", { name: "Settings" }).click();
  const st = page.getByRole("dialog", { name: "Fly Mode settings" });
  await expect(st).toContainText("shroudtopia.json");
  await expect(st.getByRole("switch", { name: "active" })).toBeChecked();
  await st.getByRole("button", { name: "Done" }).click();

  // EML and the others: no Settings item; the backend's note for a direct call
  await expect(st).toHaveCount(0);
  const emlMore = page.locator('[data-kind="eml"]').getByRole("button", { name: "More for Loot Tables X" });
  await emlMore.scrollIntoViewIfNeeded();
  await emlMore.click();
  await expect(page.getByRole("menuitem", { name: "Settings" })).toHaveCount(0);
  await page.keyboard.press("Escape");
  const eml = await api<{ format: string; note: string }>(page, "engineSettingsGet", "eml", "loot-tables-x", "ember-run", null);
  expect(eml.format).toBe("none");
  expect(eml.note).toContain("no settings");
});

test("Logs > Engines shows each plugin's state, and Restore vanilla game data puts the .kfc back", async ({ page }) => {
  await desktop(page, "/logs");
  await page.getByRole("tab", { name: "Engines" }).click();
  const list = page.getByLabel("Engine plugins");
  await expect(list.locator('[data-engine="eml"]')).toContainText("done");
  await expect(list.locator('[data-engine="eml"]')).toContainText("loot-tables-x disabled");
  await expect(list.locator('[data-engine="shroudforge"]')).toContainText("hosted");
  await expect(list.locator('[data-engine="greeneye"]')).toContainText("zoom-out started");
  await expect(list.locator('[data-engine="shroudtopia"]')).toContainText("Fly Mode active");

  // the active profile ("Ember run") has its EML mod off, and enshrouded.kfc is still patched
  const offer = page.getByRole("button", { name: "Restore vanilla game data" });
  await expect(offer).toBeVisible();
  await offer.click();
  const dialog = page.getByRole("dialog", { name: "Restore vanilla game data?" });
  await expect(dialog).toContainText("enshrouded.kfc");
  await dialog.getByRole("button", { name: "Restore", exact: true }).click();
  await expect(page.locator(".k-toast", { hasText: "Vanilla game data back" })).toBeVisible();
  await expect(offer).toHaveCount(0);
  const backups = await api<{ patched: boolean }[]>(page, "emlBackup", "client-steam");
  expect(backups.every((b) => !b.patched)).toBe(true);
});

test("no restore offer while the active profile runs an EML mod", async ({ page }) => {
  await desktop(page, "/profiles/ember-run/engines");
  await expect(page.getByRole("button", { name: "Restore vanilla game data" })).toBeVisible();
  await page.locator('[data-kind="eml"]').getByRole("button", { name: "Light Loot Tables X" }).click();
  await expect(page.getByRole("button", { name: "Restore vanilla game data" })).toHaveCount(0);
});

test("the Deck's Mods screen lists engine mods with their engine", async ({ page }) => {
  await page.goto("/?deck=steamdeck#/deck/mods");
  await expect(page.locator('[data-deck-screen="mods"]')).toBeVisible();
  await expect(page.locator('[data-deck-id="mod-shroudforge-better-chests"]')).toContainText("ShroudForge mod");
  await expect(page.locator('[data-deck-id="mod-shroudtopia-fly-mode"]')).toContainText("Shroudtopia mod");
  await expect(page.locator('[data-deck-id="mod-greeneye-zoom-out"]')).toContainText("Greeneye mod");
  await expect(page.locator('[data-deck-id="mod-eml-loot-tables-x"]')).toContainText("Off");
});
