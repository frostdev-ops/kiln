// The simple first run in browser-mock mode (`?mock=fresh`: no game known, no Kiln, no profiles, an empty library):
// the launcher looks for the game by itself, Install Kiln runs right on Play, a mod dropped anywhere on the window lands
// in the active profile ("My mods") with an Undo, a mod dropped before Kiln is installed is added after the install,
// modpack zips ask for consent, and Settings can turn the profile step off. Screenshots of the flow go to
// .out/simple/shots/ (SHOTS=0 skips them).
import { expect, test, type Locator, type Page } from "@playwright/test";
import { mkdirSync } from "node:fs";
import { join } from "node:path";

const SHOTS = join(import.meta.dirname, "..", "..", ".out", "simple", "shots");
const shots = process.env.SHOTS !== "0";
if (shots) mkdirSync(SHOTS, { recursive: true });

async function shot(page: Page, name: string, settle = 700) {
  if (!shots) return;
  await page.waitForTimeout(settle); // the hero's rise and the toast's slide
  await page.screenshot({ path: join(SHOTS, `${name}.png`) });
}

async function start(page: Page, mode: "fresh" | "empty" | null, route = "/") {
  await page.goto(mode ? `/?mock=${mode}#${route}` : `/#${route}`);
  await page.locator(".k-splash").waitFor({ state: "detached" });
}

/** Drops files (by name: mock mode) on an element, as the browser does. */
async function dropOn(page: Page, target: Locator, ...files: string[]) {
  const dt = await page.evaluateHandle((names) => { const d = new DataTransfer(); for (const n of names) d.items.add(new File(["x"], n)); return d; }, files);
  await target.dispatchEvent("dragover", { dataTransfer: dt });
  await target.dispatchEvent("drop", { dataTransfer: dt });
}

const mouth = (page: Page) => page.locator(".k-door");
const toastWith = (page: Page, text: string | RegExp) => page.locator(".k-toast").filter({ hasText: text });

/** First start up to a game with Kiln and "My mods" active. */
async function installOnPlay(page: Page) {
  await expect(mouth(page)).toHaveAccessibleName("Install Kiln", { timeout: 8000 });
  await mouth(page).click();
  await page.getByRole("dialog", { name: "Install Kiln" }).getByRole("button", { name: "Install" }).click();
  await expect(page.getByRole("button", { name: "Active profile: My mods. Change profile." })).toBeVisible({ timeout: 10_000 });
}

test("first run: finds the game, Install Kiln on Play, drop a mod into My mods, Undo", async ({ page }) => {
  const errors: string[] = [];
  page.on("pageerror", (e) => errors.push(String(e)));
  page.on("console", (m) => { if (m.type() === "error" && !m.location().url.includes("/assets-local/")) errors.push(m.text()); });
  await page.setViewportSize({ width: 1280, height: 800 });
  await start(page, "fresh");
  const hero = page.locator(".k-hero");

  // 1. nothing known yet: the launcher looks by itself (no Detect press)
  await expect(hero.getByText("Looking for Enshrouded in your Steam libraries.")).toBeVisible();
  await expect(mouth(page)).toHaveAccessibleName("Looking for Enshrouded in your Steam libraries");
  await shot(page, "1-looking", 0);

  // 2. found: the main action is Install Kiln, right here
  await expect(hero.getByText("Kiln is not in this game yet.")).toBeVisible({ timeout: 8000 });
  await expect(mouth(page)).toHaveAccessibleName("Install Kiln");
  await expect(hero.getByRole("button", { name: "Install Kiln", exact: true })).toBeVisible();
  await shot(page, "2-install");

  // 3. one confirm, the Runtime page's install task, in place
  await mouth(page).click();
  const confirm = page.getByRole("dialog", { name: "Install Kiln" });
  await expect(confirm.getByText("0.2.0-dev+20261104.0f1ffa6")).toBeVisible();
  await shot(page, "3-confirm");
  await confirm.getByRole("button", { name: "Install" }).click();
  await expect(page).toHaveURL(/#\/$/);
  await expect(mouth(page)).toHaveAccessibleName("Installing Kiln");
  await expect(hero.getByRole("status", { name: /^Install Kiln 0\.2\.0-dev/ })).toBeVisible();
  await shot(page, "4-installing");

  // 4. done: My mods is made and active, and Play is ready
  await expect(toastWith(page, "Kiln 0.2.0-dev+20261104.0f1ffa6 installed")).toBeVisible({ timeout: 10_000 });
  await expect(page.getByRole("button", { name: "Active profile: My mods. Change profile." })).toBeVisible({ timeout: 5000 });
  await expect(hero.getByText("No mods yet. Drop one anywhere on this window.")).toBeVisible();
  await expect(mouth(page)).toHaveAccessibleName("Play My mods");
  await expect(mouth(page)).toBeEnabled();
  await page.waitForTimeout(2700); // the install toast goes
  await shot(page, "5-ready");

  // 5. a mod dragged over the window: the veil says where it goes
  await page.evaluate(() => {
    const d = new DataTransfer();
    d.items.add(new File(["x"], "night-owl-1.2.0.zip"));
    const el = document.querySelector(".k-hero")!;
    (window as unknown as { __drag: ReturnType<typeof setInterval> }).__drag = setInterval(() => el.dispatchEvent(new DragEvent("dragover", { dataTransfer: d, bubbles: true, cancelable: true })), 100);
  });
  const veil = page.locator(".k-dropveil");
  await expect(veil).toBeVisible();
  await expect(veil).toContainText("Into My mods");
  await shot(page, "6-drag-over", 300);
  await page.evaluate(() => clearInterval((window as unknown as { __drag: ReturnType<typeof setInterval> }).__drag));

  // 6. dropped anywhere (here on Play): imported, in My mods, applied; the toast says so with Undo
  await dropOn(page, hero, "night-owl-1.2.0.zip");
  await expect(veil).toHaveCount(0);
  const added = toastWith(page, "Added Night Owl");
  await expect(added).toBeVisible({ timeout: 5000 });
  await expect(added.locator(".k-toast-detail")).toHaveCount(0);
  await expect(added.getByRole("button", { name: "Undo" })).toBeVisible();
  await expect(hero.getByText("1 of 1 mods load on this client.")).toBeVisible({ timeout: 5000 });
  await shot(page, "7-added");

  // 7. Undo: out of the profile again
  await added.getByRole("button", { name: "Undo" }).click();
  await expect(toastWith(page, "Removed Night Owl")).toBeVisible();
  await expect(hero.getByText("No mods yet. Drop one anywhere on this window.")).toBeVisible({ timeout: 5000 });

  // 8. dropped on another page: the same; the profile's mods list has it, lit
  await page.getByRole("navigation", { name: "Main" }).getByRole("link", { name: "Saves" }).click();
  await dropOn(page, page.locator(".k-sheet"), "night-owl-1.2.0.zip");
  await expect(toastWith(page, "Added Night Owl")).toBeVisible({ timeout: 5000 });
  await page.evaluate(() => { location.hash = "#/profiles/my-mods/mods"; });
  await expect(page.locator(".k-order-row.lit", { hasText: "night-owl" })).toBeVisible({ timeout: 5000 });
  expect(errors).toEqual([]);
});

test("a mod that needs another says which, briefly", async ({ page }) => {
  await start(page, "fresh");
  await installOnPlay(page);
  await dropOn(page, page.locator(".k-hero"), "more-hotbars-2.1.0.zip");
  const t = toastWith(page, "Added More Hotbars");
  await expect(t).toBeVisible({ timeout: 5000 });
  await expect(t.locator(".k-toast-detail")).toHaveText("Needs ui-lib.");
  // the same file again: already there
  await dropOn(page, page.locator(".k-hero"), "more-hotbars-2.1.0.zip");
  await expect(toastWith(page, "Already in My mods")).toBeVisible({ timeout: 5000 });
});

test("a mod dropped before Kiln is installed is added once Install Kiln is done", async ({ page }) => {
  await start(page, "fresh");
  await expect(mouth(page)).toHaveAccessibleName("Install Kiln", { timeout: 8000 });
  await dropOn(page, page.locator(".k-hero"), "night-owl-1.2.0.zip");
  const first = toastWith(page, "Install Kiln first");
  await expect(first).toBeVisible();
  await shot(page, "8-install-first");
  await first.getByRole("button", { name: "Install Kiln" }).click();
  await page.getByRole("dialog", { name: "Install Kiln" }).getByRole("button", { name: "Install" }).click();
  await expect(toastWith(page, "Added Night Owl")).toBeVisible({ timeout: 12_000 });
  await expect(page.getByRole("button", { name: "Active profile: My mods. Change profile." })).toBeVisible();
  await expect(page.locator(".k-hero").getByText("1 of 1 mods load on this client.")).toBeVisible({ timeout: 5000 });
});

test("nothing found: Add a folder and Look again stay", async ({ page }) => {
  await start(page, "empty");
  const hero = page.locator(".k-hero");
  await expect(hero.getByText("Kiln did not find Enshrouded in your Steam libraries.")).toBeVisible({ timeout: 8000 });
  await expect(hero.getByRole("button", { name: "Add a folder" })).toBeVisible();
  await expect(hero.getByRole("button", { name: "Look again" })).toBeVisible();
  await expect(mouth(page)).toHaveAccessibleName(/^Add game/);
  await shot(page, "0-not-found");
  await hero.getByRole("button", { name: "Add a folder" }).click();
  await expect(page.getByRole("dialog", { name: "Add a game folder" })).toBeVisible();
});

test("a modpack zip asks for consent before anything is installed", async ({ page }) => {
  await start(page, "fresh");
  await installOnPlay(page);
  await dropOn(page, page.locator(".k-hero"), "ember-isles-modpack-3.zip");
  const dialog = page.getByRole("dialog", { name: "Install this modpack?" });
  await expect(dialog).toBeVisible({ timeout: 5000 });
  await expect(dialog.getByText("This runs code on your PC")).toBeVisible();
  const install = dialog.getByRole("button", { name: "Install" });
  await expect(install).toBeDisabled();
  await shot(page, "9-modpack-consent");
  await dialog.getByText("I trust it and want its mods").click();
  await install.click();
  await expect(page.getByRole("dialog", { name: "Modpack installed" })).toBeVisible({ timeout: 8000 });
  await page.getByRole("dialog", { name: "Modpack installed" }).getByRole("button", { name: "Close" }).click();
  await expect(page.getByRole("button", { name: "Active profile: Server: Ember Isles. Change profile." })).toBeVisible({ timeout: 5000 });
});

test("players with profiles: a drop goes into the active one; the setting can turn that off", async ({ page }) => {
  await start(page, null, "/library");
  // Library's niche, setting on (the default): into the active profile (Ember run)
  await dropOn(page, page.locator("[data-drop]"), "night-owl-1.2.0.zip");
  await expect(toastWith(page, "Added Night Owl")).toBeVisible({ timeout: 5000 });
  await expect(page.getByRole("table", { name: "Library" }).getByText("Night Owl")).toBeVisible();
  await page.evaluate(() => { location.hash = "#/profiles/ember-run/mods"; });
  await expect(page.locator(".k-order-row.lit", { hasText: "night-owl" })).toBeVisible({ timeout: 5000 });
  // off: a drop is only imported
  await page.locator(".k-head-link", { hasText: "Settings" }).click();
  const sw = page.getByRole("switch", { name: /^Add new mods to the active profile/ });
  await expect(sw).toBeChecked();
  await page.getByText("Add new mods to the active profile", { exact: true }).click();
  await expect(sw).not.toBeChecked();
  await dropOn(page, page.locator(".k-sheet"), "dawn-lamp-2.0.0.zip");
  const imported = toastWith(page, "Imported 1");
  await expect(imported).toBeVisible({ timeout: 5000 });
  await expect(imported.getByRole("button", { name: "Library" })).toBeVisible();
  await page.evaluate(() => { location.hash = "#/profiles/ember-run/mods"; });
  await expect(page.locator(".k-order-row", { hasText: "night-owl" })).toBeVisible({ timeout: 5000 });
  await expect(page.locator(".k-order-row", { hasText: "dawn-lamp" })).toHaveCount(0);
});

test("the first run's Play states fit 1024×640", async ({ page }) => {
  await page.setViewportSize({ width: 1024, height: 640 });
  await start(page, "fresh");
  const fits = async (what: string) => {
    const over = await page.evaluate(() => ({ x: document.documentElement.scrollWidth > innerWidth + 1, y: document.documentElement.scrollHeight > innerHeight + 1 }));
    expect(over, what).toEqual({ x: false, y: false });
    // the sentence and its links stay clear of the mouth
    const say = await page.locator(".k-hero-say").boundingBox();
    const door = await mouth(page).boundingBox();
    expect(say && door && say.x + say.width <= door.x + 1, what).toBeTruthy();
  };
  await fits("looking");
  await expect(mouth(page)).toHaveAccessibleName("Install Kiln", { timeout: 8000 });
  await fits("install");
  await mouth(page).click();
  await page.getByRole("dialog", { name: "Install Kiln" }).getByRole("button", { name: "Install" }).click();
  await expect(page.locator(".k-hero .k-task")).toBeVisible();
  await fits("installing");
  await shot(page, "4b-installing-1024x640");
  await expect(page.getByRole("button", { name: "Active profile: My mods. Change profile." })).toBeVisible({ timeout: 10_000 });
  await fits("ready");
  await shot(page, "5b-ready-1024x640", 2800);
});
