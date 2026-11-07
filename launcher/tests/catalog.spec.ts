// Smoke (browser mock, src/api/mockNexus.ts): the online catalog on Nexus Mods (docs/kiln/launcher/catalog.md) in the
// desktop UI and in Deck mode. Kiln mods by default, every Enshrouded mod and the installed ones on demand, search;
// a mod's details; free accounts open the file's page (Mod Manager Download), premium accounts add in the launcher;
// an nxm link adds the mod with Undo and "Get" for what it needs; updates are a click; the API key and the nxm opt-in
// in Settings. Screenshots go to $SHOTS_DIR, default .out/catalog/shots/ (SHOTS=0 skips).
import { expect, test, type Page } from "@playwright/test";
import { mkdirSync } from "node:fs";
import { join } from "node:path";

const SHOTS = process.env.SHOTS_DIR || join(import.meta.dirname, "..", "..", ".out", "catalog", "shots");

async function shot(page: Page, name: string) {
  if (process.env.SHOTS === "0") return;
  mkdirSync(SHOTS, { recursive: true });
  await page.waitForTimeout(450);
  await page.screenshot({ path: join(SHOTS, `${name}.png`) });
}

async function open(page: Page, query = "", route = "/catalog") {
  await page.goto(`/${query}#${route}`);
  await page.locator(".k-splash").waitFor({ state: "detached" });
}

const opened = (page: Page) => page.evaluate(() => (window as unknown as { __kilnMock: { opened: () => string[] } }).__kilnMock.opened());
const nxm = (page: Page, url: string) => page.evaluate((u) => (window as unknown as { __kilnNexusMock: { nxm: (u: string) => boolean } }).__kilnNexusMock.nxm(u), url);
const future = () => Math.floor(Date.now() / 1000) + 3600;
const grid = (page: Page) => page.getByRole("list", { name: "Mods" });
const card = (page: Page, name: string) => grid(page).getByRole("listitem", { name, exact: true });

test.describe("desktop", () => {
  test("Kiln mods by default; all, installed and search; an update is shown, never applied by itself", async ({ page }) => {
    await open(page);
    await expect(page.getByRole("navigation", { name: "Main" }).getByRole("link", { name: "Catalog" })).toHaveClass(/active|on/);
    await expect(card(page, "Lantern Glow")).toBeVisible();
    for (const n of ["Night Sky", "Ember Core", "Weather Plus", "Cozy Camp"]) await expect(card(page, n)).toBeVisible();
    // not Kiln mods: left out by default, Kiln's own page too
    await expect(card(page, "Bigger Bags EMF")).toHaveCount(0);
    await expect(card(page, "HD Texture Pack")).toHaveCount(0);
    await expect(grid(page).getByRole("listitem", { name: /^Kiln - / })).toHaveCount(0);
    // the installed one with a newer file: a banner and its card say so
    await expect(card(page, "Weather Plus")).toContainText("Update 1.1.0");
    await expect(page.getByText("Update ready: Weather Plus")).toBeVisible();
    await shot(page, "desktop-catalog");

    await page.getByRole("radio", { name: "All Enshrouded" }).or(page.getByRole("tab", { name: "All Enshrouded" })).or(page.getByRole("button", { name: "All Enshrouded" })).first().click();
    await expect(card(page, "HD Texture Pack")).toBeVisible();
    await expect(card(page, "Bigger Bags EMF")).toContainText("EMF mod");
    await page.getByLabel("Search").fill("night");
    await expect(card(page, "Night Sky")).toBeVisible();
    await expect(grid(page).getByRole("listitem")).toHaveCount(1);
    await page.getByLabel("Search").fill("");
    await page.getByRole("radio", { name: /Installed/ }).or(page.getByRole("tab", { name: /Installed/ })).or(page.getByRole("button", { name: /^Installed/ })).first().click();
    await expect(grid(page).getByRole("listitem")).toHaveCount(1);
    await expect(card(page, "Weather Plus")).toBeVisible();
  });

  test("details; a free account opens the file's page with its Mod Manager Download", async ({ page }) => {
    await open(page);
    await card(page, "Lantern Glow").click();
    const d = page.getByRole("dialog", { name: "Lantern Glow" });
    await expect(d).toContainText("Warm light for the long nights.");
    await expect(d).toContainText("emberwright");
    await expect(d.locator(".k-cat-reqs")).toContainText("Ember Core");
    await expect(d.locator(".k-cat-reqs")).toContainText("Not installed");
    await expect(d.locator(".k-cat-reqs")).not.toContainText("Kiln");
    await expect(d.locator(".k-cat-hero")).toBeVisible();
    await shot(page, "desktop-details-free");
    await d.getByRole("button", { name: "Download on Nexus Mods" }).click();
    await expect.poll(() => opened(page)).toEqual(["https://www.nexusmods.com/enshrouded/mods/310?tab=files&file_id=1001&nmm=1"]);
    await expect(page.locator(".k-toast").filter({ hasText: "Opened on Nexus Mods" })).toBeVisible();
  });

  test("an nxm link adds the mod: Undo, and Get for what it needs", async ({ page }) => {
    await open(page, "", "/");
    expect(await nxm(page, `nxm://enshrouded/mods/310/files/1001?key=abc&expires=${future()}&user_id=1`)).toBe(true);
    const t = page.locator(".k-toast").filter({ hasText: "Added Lantern Glow" });
    await expect(t).toBeVisible();
    await expect(t).toContainText("Needs Ember Core.");
    await shot(page, "desktop-nxm-added");
    await t.getByRole("button", { name: "Get Ember Core" }).click();
    await expect(page.getByRole("dialog", { name: "Ember Core" })).toBeVisible();
    await page.keyboard.press("Escape");
    // it is in the active profile now, and the catalog says installed
    await open(page, "", "/catalog");
    await expect(card(page, "Lantern Glow")).toContainText("Installed");
    // an expired link is refused
    await nxm(page, "nxm://enshrouded/mods/313/files/1003?key=abc&expires=1000");
    await expect(page.locator(".k-toast").filter({ hasText: "expired" })).toBeVisible();
    // another game's link is not Kiln's
    expect(await nxm(page, "nxm://skyrimspecialedition/mods/1/files/2?key=x&expires=1")).toBe(false);
  });

  test("Undo takes the mod out of the profile again", async ({ page }) => {
    await open(page, "", "/");
    await nxm(page, `nxm://enshrouded/mods/317/files/1017?key=abc&expires=${future()}`);
    const t = page.locator(".k-toast").filter({ hasText: "Added Cozy Camp" });
    await t.getByRole("button", { name: "Undo" }).click();
    await expect(page.locator(".k-toast").filter({ hasText: "Removed Cozy Camp" })).toBeVisible();
  });

  test("a waiting nxm link resumes when Kiln becomes ready", async ({ page }) => {
    await open(page, "?mock=fresh", "/");
    await expect(page.locator(".k-door")).toHaveAccessibleName("Install Kiln", { timeout: 8000 });
    await nxm(page, `nxm://enshrouded/mods/313/files/1003?key=abc&expires=${future()}`);
    await expect(page.locator(".k-toast").filter({ hasText: "A Nexus Mods download is waiting" })).toBeVisible();
    await page.locator(".k-door").click();
    await page.getByRole("dialog", { name: "Install Kiln" }).getByRole("button", { name: "Install", exact: true }).click();
    await expect(page.locator(".k-toast").filter({ hasText: "Added Night Sky" })).toBeVisible({ timeout: 12_000 });
  });

  test("premium: Add downloads here; Update is a click", async ({ page }) => {
    await open(page, "?nexus=premium");
    await card(page, "Weather Plus").click();
    const d = page.getByRole("dialog", { name: "Weather Plus" });
    await expect(d).toContainText("Installed");
    await d.getByRole("button", { name: "Update to 1.1.0" }).click();
    await expect(page.locator(".k-toast").filter({ hasText: "Updated Weather Plus" })).toBeVisible();
    await expect(card(page, "Weather Plus")).toContainText("Installed");
    await expect(card(page, "Weather Plus")).not.toContainText("Update");
    await card(page, "Night Sky").click();
    await page.getByRole("dialog", { name: "Night Sky" }).getByRole("button", { name: "Add", exact: true }).click();
    await expect(page.locator(".k-toast").filter({ hasText: "Added Night Sky" })).toBeVisible();
    expect(await opened(page)).toEqual([]);
  });

  test("no key: everything listed, Kiln mods need the key; Settings takes it and the nxm opt-in", async ({ page }) => {
    await open(page, "?nexus=nokey");
    await expect(page.getByText("Add your Nexus Mods API key")).toBeVisible();
    await expect(page.getByText("Kiln mods need your API key")).toBeVisible();
    await page.getByRole("button", { name: "Show all" }).click();
    await expect(card(page, "Lantern Glow")).toBeVisible();
    await shot(page, "desktop-nokey");

    await open(page, "?nexus=nokey", "/settings");
    const c = page.locator(".k-card", { has: page.getByRole("heading", { name: "Nexus Mods" }) });
    await expect(c.getByTestId("nexus-account")).toContainText("No API key yet.");
    await c.getByLabel("Nexus Mods API key").fill("bad");
    await c.getByRole("button", { name: "Save" }).click();
    await expect(page.locator(".k-toast").filter({ hasText: "Key not saved" })).toBeVisible();
    await c.getByLabel("Nexus Mods API key").fill("my-personal-key");
    await c.getByRole("button", { name: "Save" }).click();
    await expect(c.getByTestId("nexus-account")).toContainText("Signed in as embervale_player");
    await expect(c.getByTestId("nexus-account")).toContainText("Free");
    await expect(c).toContainText("Kept in your system keyring.");
    await expect(c.getByLabel("Nexus Mods API key")).toHaveValue("");
    const sw = c.getByRole("switch");
    await expect(sw).not.toBeChecked();
    await expect(c).toContainText("Other games' links go to Vortex");
    await c.getByText("Mod Manager Download links").click();
    await expect(sw).toBeChecked();
    await shot(page, "desktop-settings");
  });

  test("the Nexus Mods build downloads mods too (no launcher update anywhere)", async ({ page }) => {
    await open(page, "?dist=nexus&nexus=premium");
    await card(page, "Cozy Camp").click();
    await page.getByRole("dialog", { name: "Cozy Camp" }).getByRole("button", { name: "Add", exact: true }).click();
    await expect(page.locator(".k-toast").filter({ hasText: "Added Cozy Camp" })).toBeVisible();
    await expect(page.locator(".k-upd")).toHaveCount(0);
  });
});

test.describe("Deck mode", () => {
  test.use({ viewport: { width: 1280, height: 800 } });

  async function pad(page: Page, button: string) {
    const send = (pressed: boolean) => page.evaluate(([b, p]) => (window as unknown as { __kilnMock: { emit: (n: string, v: unknown) => void } }).__kilnMock
      .emit("kiln://gamepad", { kind: "button", button: b, pressed: p, repeat: false, device: "event9" }), [button, pressed] as const);
    await send(true);
    await send(false);
    await page.waitForTimeout(60);
  }
  const focused = (page: Page) => page.evaluate(() => (document.activeElement as HTMLElement | null)?.dataset?.deckId ?? null);
  const screen = (page: Page) => page.evaluate(() => document.querySelector("[data-deck-screen]")?.getAttribute("data-deck-screen") ?? null);

  test("the Catalog tab with the pad: rows, X and Y, a mod's page, Add (premium), the added dialog", async ({ page }) => {
    await page.goto("/?deck=steamdeck&nexus=premium#/");
    await expect.poll(() => focused(page)).toBe("mouth");
    for (let i = 0; i < 3; i++) await pad(page, "rb");
    await expect.poll(() => screen(page)).toBe("catalog");
    await expect.poll(() => focused(page)).toBe("cat-310");
    await expect(page.locator('[data-deck-id="cat-316"]')).toContainText("Update");
    await expect(page.locator('[data-deck-id="cat-312"]')).toHaveCount(0);
    await shot(page, "deck-catalog");
    // X: all Enshrouded mods
    await pad(page, "x");
    await expect(page.locator('[data-deck-id="cat-312"]')).toBeVisible();
    await pad(page, "x");
    await expect(page.locator(".deck-say")).toContainText("Installed");
    await pad(page, "x");
    await expect(page.locator(".deck-say")).toContainText("Kiln mods");
    // A opens Lantern Glow, A on Add adds it; the dialog offers Undo and Get Ember Core
    await page.locator('[data-deck-id="cat-310"]').focus();
    await pad(page, "a");
    const d = page.locator('[data-deck-dialog="catalog-mod"]');
    await expect(d).toContainText("Needs Ember Core");
    await expect.poll(() => focused(page)).toBe("mod-add");
    await shot(page, "deck-mod");
    await pad(page, "a");
    const added = page.locator('[data-deck-dialog="catalog-added"]');
    await expect(added).toContainText("Added Lantern Glow");
    await expect(added.locator('[data-deck-id="added-undo"]')).toBeVisible();
    await shot(page, "deck-added");
    await added.locator('[data-deck-id="added-get-320"]').focus();
    await pad(page, "a");
    await expect(page.locator('[data-deck-dialog="catalog-mod"]')).toContainText("emberwright");
    await expect(page.locator(".deck-dialog-title")).toHaveText("Ember Core");
    await pad(page, "b");
    await expect(page.locator('[data-deck-dialog="catalog-mod"]')).toHaveCount(0);
    await pad(page, "b");
    await expect.poll(() => screen(page)).toBe("home");
  });

  test("free account: Open on Nexus Mods; an nxm link shows the added dialog", async ({ page }) => {
    await page.goto("/?deck=steamdeck#/deck/catalog");
    await expect.poll(() => focused(page)).toBe("cat-310");
    await pad(page, "a");
    await expect.poll(() => focused(page)).toBe("mod-site");
    await pad(page, "a");
    await expect.poll(() => opened(page)).toEqual(["https://www.nexusmods.com/enshrouded/mods/310?tab=files&file_id=1001&nmm=1"]);
    await pad(page, "b");
    await nxm(page, `nxm://enshrouded/mods/313/files/1003?key=k&expires=${future()}`);
    const added = page.locator('[data-deck-dialog="catalog-added"]');
    await expect(added).toContainText("Added Night Sky");
    await pad(page, "a");
    await expect(added).toHaveCount(0);
  });

  test("a running Add keeps the dialog open from keyboard and pad controls", async ({ page }) => {
    await page.goto("/?deck=steamdeck&nexus=premium#/deck/catalog");
    await expect.poll(() => focused(page)).toBe("cat-310");
    await page.keyboard.press("Enter");
    await expect.poll(() => focused(page)).toBe("mod-add");
    await page.keyboard.press("Enter");
    const d = page.locator('[data-deck-dialog="catalog-mod"]');
    await expect(d.getByRole("button", { name: "Adding…" })).toBeVisible();
    await expect(d.getByRole("button", { name: "Close", exact: true })).toBeDisabled();
    await page.keyboard.press("Escape");
    await expect(d).toBeVisible();
    await expect(page.locator('[data-deck-dialog="catalog-added"]')).toContainText("Added Lantern Glow");
  });

  test("each nxm result keeps its Undo dialog until acknowledged", async ({ page }) => {
    await page.goto("/?deck=steamdeck#/deck/catalog");
    await expect.poll(() => focused(page)).toBe("cat-310");
    await nxm(page, `nxm://enshrouded/mods/313/files/1003?key=k&expires=${future()}`);
    const added = page.locator('[data-deck-dialog="catalog-added"]');
    await expect(added).toContainText("Added Night Sky");
    await nxm(page, `nxm://enshrouded/mods/317/files/1017?key=k&expires=${future()}`);
    await expect.poll(() => page.evaluate(() => (window as unknown as { __kilnNexusMock: { state: () => { installed: unknown[] } } }).__kilnNexusMock.state().installed.length)).toBe(3);
    await expect(added).toContainText("Added Night Sky");
    await page.keyboard.press("Enter");
    await expect(added).toContainText("Added Cozy Camp");
    await expect.poll(() => focused(page)).toBe("added-ok");
    await page.keyboard.press("Enter");
    await expect(added).toHaveCount(0);
  });

  test("switching UI during an nxm task preserves the added dialog", async ({ page }) => {
    await open(page, "", "/");
    await nxm(page, `nxm://enshrouded/mods/313/files/1003?key=k&expires=${future()}`);
    await expect.poll(() => page.evaluate(() => (window as unknown as { __kilnNexusMock: { state: () => { links: number } } }).__kilnNexusMock.state().links)).toBe(0);
    await page.evaluate(() => { location.hash = "#/deck"; });
    await expect(page.locator(".deck")).toBeVisible();
    await expect(page.locator('[data-deck-dialog="catalog-added"]')).toContainText("Added Night Sky");
  });
});
