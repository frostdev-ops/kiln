// Smoke (browser mock): the Nexus Mods build (`?dist=nexus`, app_info.distribution "nexus"; api/distribution.ts) has
// no self-update UI at all: no header word, no notice (even when an update status arrives), no Updates settings, no
// Check now, in the desktop and the Deck UI; no update_* command is called. Settings, About and Deck Settings show
// "New versions: Nexus Mods", whose link opens the Kiln page on Nexus Mods. The direct build keeps its update UI.
// Screenshots go to $SHOTS_DIR, default .out/launcher/shots/ (SHOTS=0 skips).
import { expect, test, type Page } from "@playwright/test";
import { mkdirSync } from "node:fs";
import { join } from "node:path";

const SHOTS = process.env.SHOTS_DIR || join(import.meta.dirname, "..", "..", ".out", "launcher", "shots");
const NEXUS_PAGE = "https://www.nexusmods.com/enshrouded/mods/301";
const NEXUS = "/?dist=nexus";

async function open(page: Page, route = "/", query = NEXUS) {
  await page.goto(`${query}#${route}`);
  await page.locator(".k-splash").waitFor({ state: "detached" });
}

async function shot(page: Page, name: string) {
  if (process.env.SHOTS === "0") return;
  mkdirSync(SHOTS, { recursive: true });
  await page.waitForTimeout(400);
  await page.screenshot({ path: join(SHOTS, `mock-${name}.png`) });
}

type UpdateMock = Record<string, (...x: unknown[]) => unknown>;
const updateMock = (page: Page, fn: string, ...args: unknown[]) =>
  page.evaluate(([f, a]) => (window as unknown as { __kilnUpdateMock: UpdateMock }).__kilnUpdateMock[f as string](...(a as unknown[])), [fn, args] as const);
const updateCalls = (page: Page) => updateMock(page, "calls") as Promise<string[]>;
const opened = (page: Page) => page.evaluate(() => (window as unknown as { __kilnMock: { opened: () => string[] } }).__kilnMock.opened());

test("Nexus build: no update word or notice in the header, even when an update status arrives", async ({ page }) => {
  await open(page);
  await expect(page.locator(".k-head")).toBeVisible();
  await expect(page.locator(".k-upd")).toHaveCount(0);
  // a status that would open the notice in a direct build (as kiln://update): the Nexus build mounts no update feed
  await updateMock(page, "set", { phase: "available", available: { version: "0.2.0-dev.43", notes: null, date: null, source: "frostdev.io", page: "https://frostdev.io/kiln", runtime: null } });
  await page.waitForTimeout(500);
  await expect(page.locator(".k-upd")).toHaveCount(0);
  await expect(page.getByRole("dialog", { name: /Kiln 0\.2\.0-dev\.43/ })).toHaveCount(0);
  await expect(page.getByRole("button", { name: /Launcher update/ })).toHaveCount(0);
  expect(await updateCalls(page)).toEqual([]);
});

test("Nexus build: Settings and About show only 'New versions: Nexus Mods', which opens the Kiln page", async ({ page }) => {
  await open(page, "/settings");
  const card = page.locator(".k-card", { has: page.getByRole("heading", { name: "Updates" }) });
  await expect(card.getByTestId("nexus-versions")).toHaveText("New versions: Nexus Mods");
  await expect(card.getByRole("button", { name: "Check now" })).toHaveCount(0);
  await expect(card.getByRole("switch")).toHaveCount(0);
  await expect(card.getByLabel("Channel")).toHaveCount(0);
  await expect(page.getByText("Check automatically")).toHaveCount(0);
  await shot(page, "nexus-settings");
  await card.getByRole("button", { name: "Nexus Mods" }).click();
  await expect.poll(() => opened(page)).toEqual([NEXUS_PAGE]);

  await open(page, "/about");
  await expect(page.getByText("New versions", { exact: true })).toBeVisible();
  await expect(page.getByTestId("nexus-page")).toHaveText("Nexus Mods");
  await expect(page.getByRole("button", { name: "Check now" })).toHaveCount(0);
  await expect(page.getByText("Up to date")).toHaveCount(0);
  await shot(page, "nexus-about");
  await page.getByTestId("nexus-page").click();
  await expect.poll(() => opened(page)).toEqual([NEXUS_PAGE]);
  expect(await updateCalls(page)).toEqual([]);
});

test.describe("Deck mode", () => {
  test.use({ viewport: { width: 1280, height: 800 } });

  test("Nexus build: Deck Settings has 'New versions: Nexus Mods' instead of launcher updates", async ({ page }) => {
    await page.goto("/?dist=nexus&deck=steamdeck#/deck/settings");
    await expect(page.locator('[data-deck-screen="settings"]')).toBeVisible();
    await expect(page.locator('[data-deck-id="set-quit"]')).toBeVisible();
    await expect(page.locator('[data-deck-id="set-update"]')).toHaveCount(0);
    const row = page.locator('[data-deck-id="set-versions"]');
    await expect(row).toContainText("New versions");
    await expect(row).toContainText("Nexus Mods");
    await expect(row).toHaveAttribute("data-deck-a", "Open");
    await expect(page.getByText("Launcher updates")).toHaveCount(0);
    await shot(page, "nexus-deck-settings");
    await row.click();
    await expect.poll(() => opened(page)).toEqual([NEXUS_PAGE]);
    // the Deck's Home has no launcher update either
    await page.goto("/?dist=nexus&deck=steamdeck#/deck");
    await expect(page.locator('[data-deck-screen="home"]')).toBeVisible();
    await expect(page.getByText(/Launcher update/i)).toHaveCount(0);
    expect(await updateCalls(page)).toEqual([]);
  });

  test("direct build: Deck Settings keeps launcher updates and has no Nexus row", async ({ page }) => {
    await page.goto("/?deck=steamdeck#/deck/settings");
    await expect(page.locator('[data-deck-id="set-update"]')).toContainText("Launcher updates");
    await expect(page.locator('[data-deck-id="set-versions"]')).toHaveCount(0);
  });
});

test("direct build: the update UI stays and the Nexus line is absent", async ({ page }) => {
  await open(page, "/about", "/");
  await expect(page.getByText("Up to date ·")).toBeVisible();
  await expect(page.getByRole("button", { name: "Check now" })).toBeVisible();
  await expect(page.getByTestId("nexus-page")).toHaveCount(0);
  await open(page, "/settings", "/");
  const card = page.locator(".k-card", { has: page.getByRole("heading", { name: "Updates" }) });
  await expect(card.getByRole("switch", { name: /Check automatically/ })).toBeChecked();
  await expect(card.getByTestId("nexus-versions")).toHaveCount(0);
  expect(await updateCalls(page)).toContain("update_status");
});
