// Smoke (browser mock): the launcher update notice, one line and its buttons per state. Found → the notice opens
// under the header → Update → Updating… → Ready to restart → Restart → Updated to … → Update game (the runtime).
// Also: Skip, a .deb install (Download), the game running, a failed download (a short toast), Settings and About.
// Screenshots go to $SHOTS_DIR, default .out/launcher/shots/ (SHOTS=0 skips).
import { expect, test, type Page } from "@playwright/test";
import { mkdirSync } from "node:fs";
import { join } from "node:path";

const SHOTS = process.env.SHOTS_DIR || join(import.meta.dirname, "..", "..", ".out", "launcher", "shots");

async function open(page: Page, route = "/") {
  await page.goto(`/#${route}`);
  await page.locator(".k-splash").waitFor({ state: "detached" });
}

async function shot(page: Page, name: string) {
  if (process.env.SHOTS === "0") return;
  mkdirSync(SHOTS, { recursive: true });
  await page.waitForTimeout(400);
  await page.screenshot({ path: join(SHOTS, `mock-${name}.png`) });
}

const mock = (page: Page, fn: string, ...args: unknown[]) =>
  page.evaluate(([f, a]) => (window as unknown as { __kilnUpdateMock: Record<string, (...x: unknown[]) => unknown> }).__kilnUpdateMock[f as string](...(a as unknown[])), [fn, args] as const);
const game = (page: Page, fn: string, ...args: unknown[]) =>
  page.evaluate(([f, a]) => (window as unknown as { __kilnMock: Record<string, (...x: unknown[]) => unknown> }).__kilnMock[f as string](...(a as unknown[])), [fn, args] as const);
const word = (page: Page, w: string) => page.getByRole("button", { name: `Launcher update: ${w}`, exact: true });

test("an update: notice, Update, Restart, then Update game", async ({ page }) => {
  await open(page);
  await expect(page.locator(".k-upd")).toHaveCount(0);
  await mock(page, "offer", "0.2.0-dev.43", "Notes that the notice does not show.", "0.2.0-dev+20261106.f3e65ec");
  const offer = page.getByRole("dialog", { name: "Kiln 0.2.0-dev.43" });
  await expect(offer).toBeVisible();
  await expect(offer.getByRole("button")).toHaveText(["", "Skip", "Update"]);
  await expect(offer).not.toContainText("Notes");
  await expect(word(page, "Update")).toBeVisible();
  await shot(page, "update-available");

  await offer.getByRole("button", { name: "Update", exact: true }).click();
  const updating = page.getByRole("dialog", { name: "Updating…" });
  await expect(updating.getByRole("progressbar")).toBeVisible();
  await expect(word(page, "Updating")).toBeVisible();
  await shot(page, "update-downloading");
  const ready = page.getByRole("dialog", { name: "Ready to restart" });
  await expect(ready).toBeVisible({ timeout: 8000 });
  await expect(ready.getByRole("button")).toHaveText(["", "Later", "Restart"]);
  await expect(word(page, "Restart")).toBeVisible();
  await shot(page, "update-ready");

  await game(page, "setUpgradeAvailable", "0.2.0-dev+20261104.0f1ffa6"); // a bundle the mock has
  await ready.getByRole("button", { name: "Restart", exact: true }).click();
  const updated = page.getByRole("dialog", { name: "Updated to 0.2.0-dev.43" });
  await expect(updated).toBeVisible();
  await expect(updated.getByRole("button", { name: "Update game" })).toBeEnabled({ timeout: 6000 });
  await expect(word(page, "Updated")).toBeVisible();
  await shot(page, "update-installed-runtime-next");
  await updated.getByRole("button", { name: "Update game" }).click();
  await expect(page.locator(".k-toast", { hasText: "Kiln 0.2.0-dev+20261104.0f1ffa6 installed" })).toBeVisible({ timeout: 8000 });
  await expect(page.locator(".k-upd")).toHaveCount(0);
});

test("Skip hides the version until a newer one; Settings can undo", async ({ page }) => {
  await open(page);
  await mock(page, "offer", "0.2.0-dev.43");
  await page.getByRole("dialog", { name: "Kiln 0.2.0-dev.43" }).getByRole("button", { name: "Skip" }).click();
  await expect(page.locator(".k-upd")).toHaveCount(0);
  await open(page, "/settings");
  const card = page.locator(".k-card", { has: page.getByRole("heading", { name: "Updates" }) });
  await expect(card.getByText("Skipped 0.2.0-dev.43")).toBeVisible();
  await expect(card.getByRole("switch", { name: /Check automatically/ })).toBeChecked();
  await shot(page, "update-settings");
  await card.getByRole("button", { name: "Undo" }).click();
  await expect(card.getByText("0.2.0-dev.43 available")).toBeVisible();
  await mock(page, "offer", "0.2.0-dev.44");
  await expect(page.getByRole("dialog", { name: "Kiln 0.2.0-dev.44" })).toBeVisible();
});

test("a .deb install offers Download; dismiss keeps the header word", async ({ page }) => {
  await open(page);
  await mock(page, "set", { install: { kind: "deb", inPlace: false, reason: "Installed from a .deb package." } });
  await mock(page, "offer", "0.2.0-dev.43");
  const offer = page.getByRole("dialog", { name: "Kiln 0.2.0-dev.43" });
  await expect(offer.getByRole("button")).toHaveText(["", "Skip", "Download"]);
  await shot(page, "update-deb");
  await offer.getByRole("button", { name: "Dismiss" }).click();
  await expect(offer).toHaveCount(0);
  await word(page, "Update").click();
  await expect(offer).toBeVisible();
});

test("a failed download is a short toast; Update game waits for the game to close", async ({ page }) => {
  await open(page);
  await mock(page, "failNextInstall", [
    { source: "frostdev.io", step: "signature", message: "the signature does not match the file" },
    { source: "GitHub", step: "download", message: "not reachable" },
  ]);
  await mock(page, "offer", "0.2.0-dev.43");
  await page.getByRole("dialog", { name: "Kiln 0.2.0-dev.43" }).getByRole("button", { name: "Update", exact: true }).click();
  const toast = page.locator(".k-toast-err");
  await expect(toast).toHaveText(/Update failed/, { timeout: 8000 });
  await expect(toast).not.toContainText("signature");
  await expect(page.getByRole("dialog", { name: "Kiln 0.2.0-dev.43" }).getByRole("button", { name: "Update", exact: true })).toBeVisible();
  await shot(page, "update-failed");

  await page.locator(".k-door").click();
  await expect(page.locator(".k-head-state")).toHaveText(/^Running/, { timeout: 8000 });
  await game(page, "setUpgradeAvailable", "0.2.0-dev+20261106.f3e65ec");
  await mock(page, "justUpdated", "0.2.0-dev.41");
  const updated = page.getByRole("dialog", { name: "Updated to 0.2.0-dev.42" });
  const button = updated.getByRole("button", { name: "Update game" });
  await expect(button).toBeDisabled();
  await expect(button).toHaveAttribute("title", "Close the game first");
  await shot(page, "update-game-running");
  await game(page, "stopGame");
  await expect(button).toBeEnabled({ timeout: 6000 });
});

test("About shows the state with Check now", async ({ page }) => {
  await open(page, "/about");
  await expect(page.getByText("Up to date ·")).toBeVisible();
  await page.getByRole("button", { name: "Check now" }).click();
  await expect(page.locator(".k-toast", { hasText: "Up to date" })).toBeVisible();
});
